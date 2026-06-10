// ═══════════════════════════════════════════════════════════════════════════
// BACKFILL SERVICE
// ───────────────────────────────────────────────────────────────────────────
// Reads historical data from KepServerEX via OPC HDA COM and writes it
// to TimescaleDB in configurable chunks. Runs on the MTA thread via
// StaThreadDispatcher — the actual HDA COM call is on the STA thread,
// DB writes happen on the calling thread.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Threading;
using OpcHdaBroker.ComInterop;
using Serilog;

namespace OpcHdaBroker.TimescaleDb
{
    /// <summary>
    /// Backfill progress and status.
    /// </summary>
    public class BackfillStatus
    {
        public bool       IsRunning     { get; set; }
        public bool       IsPaused      { get; set; }
        public DateTime?  StartTime     { get; set; }
        public DateTime?  EndTime       { get; set; }
        public DateTime?  CurrentTime   { get; set; }
        public long       TotalPoints   { get; set; }
        public int        TagsProcessed { get; set; }
        public int        TotalTags     { get; set; }
        public string     State         { get; set; }
        public TimeSpan   Elapsed       { get; set; }
        public double     ProgressPct   { get; set; }
        public string     EstimatedRemaining { get; set; }
    }

    /// <summary>
    /// Backfill configuration from App.config.
    /// </summary>
    public class BackfillConfig
    {
        public bool       Enabled       { get; set; }
        public DateTime   StartDate     { get; set; }
        public DateTime   EndDate       { get; set; }
        public int        ChunkDays     { get; set; }
        public int        MaxPointsPerCall { get; set; }
        public int        BatchSize     { get; set; }
        public int        PauseBetweenChunksMs { get; set; }
        public bool       AutoStart     { get; set; }
        public string     BrokerId      { get; set; }

        public static BackfillConfig FromAppSettings()
        {
            return new BackfillConfig
            {
                Enabled       = bool.TryParse(ConfigurationManager.AppSettings["Backfill.Enabled"], out var e) && e,
                StartDate     = DateTime.TryParse(ConfigurationManager.AppSettings["Backfill.StartDate"], out var s) ? s.ToUniversalTime() : DateTime.UtcNow.AddYears(-1),
                EndDate       = DateTime.TryParse(ConfigurationManager.AppSettings["Backfill.EndDate"], out var end) ? end.ToUniversalTime() : DateTime.UtcNow,
                ChunkDays     = int.TryParse(ConfigurationManager.AppSettings["Backfill.ChunkDays"], out var cd) ? cd : 30,
                MaxPointsPerCall = int.TryParse(ConfigurationManager.AppSettings["Backfill.MaxPointsPerCall"], out var mp) ? mp : 50000,
                BatchSize     = int.TryParse(ConfigurationManager.AppSettings["Backfill.BatchSize"], out var bs) ? bs : 500,
                PauseBetweenChunksMs = int.TryParse(ConfigurationManager.AppSettings["Backfill.PauseBetweenChunksMs"], out var p) ? p : 500,
                AutoStart     = bool.TryParse(ConfigurationManager.AppSettings["Backfill.AutoStart"], out var a) && a,
                BrokerId      = ConfigurationManager.AppSettings["Ingestion.BrokerId"] ?? "kepserver01"
            };
        }
    }

    /// <summary>
    /// Manages backfill of historical HDA data to TimescaleDB.
    /// Thread-safe — can be paused/resumed from any thread.
    /// </summary>
    public class BackfillService
    {
        private static readonly ILogger Log = Serilog.Log.ForContext<BackfillService>();

        private readonly TsdbRepository  _tsdb;
        private readonly BackfillConfig  _config;
        private readonly HdaReader       _reader;
        private readonly IList<string>   _tags;

        private readonly object          _stateLock = new object();
        private BackfillStatus           _status = new BackfillStatus();
        private CancellationTokenSource  _cts;
        private ManualResetEventSlim     _pauseEvent = new ManualResetEventSlim(true);

        public BackfillService(TsdbRepository tsdb, BackfillConfig config, HdaReader reader, IList<string> tags)
        {
            _tsdb   = tsdb ?? throw new ArgumentNullException(nameof(tsdb));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _tags   = tags ?? new List<string>();
        }

        /// <summary>
        /// Current backfill status. Safe to read from any thread.
        /// </summary>
        public BackfillStatus Status
        {
            get { lock (_stateLock) { return CloneStatus(); } }
        }

        /// <summary>
        /// Start the backfill in a background thread.
        /// </summary>
        public void StartAsync()
        {
            lock (_stateLock)
            {
                if (_status.IsRunning) return;
                _cts = new CancellationTokenSource();
                _status.IsRunning = true;
                _status.IsPaused  = false;
                _status.StartTime = DateTime.UtcNow;
                _status.State     = "Starting";
                _status.TotalPoints   = 0;
                _status.TagsProcessed = 0;
                _pauseEvent.Set();
            }

            ThreadPool.QueueUserWorkItem(_ => RunBackfill(_cts.Token));
            Log.Information("Backfill started: {Start} → {End}, ChunkDays={Days}",
                _config.StartDate, _config.EndDate, _config.ChunkDays);
        }

        /// <summary>
        /// Pause the backfill. Resumes from current position.
        /// </summary>
        public void Pause()
        {
            lock (_stateLock)
            {
                if (!_status.IsRunning) return;
                _status.IsPaused = true;
                _pauseEvent.Reset();
                _status.State = "Paused";
            }
            Log.Information("Backfill paused at {Time}", _status.CurrentTime);
        }

        /// <summary>
        /// Resume a paused backfill.
        /// </summary>
        public void Resume()
        {
            lock (_stateLock)
            {
                if (!_status.IsRunning) return;
                _status.IsPaused = false;
                _pauseEvent.Set();
                _status.State = "Running";
            }
            Log.Information("Backfill resumed");
        }

        /// <summary>
        /// Stop and cancel the backfill.
        /// </summary>
        public void Stop()
        {
            lock (_stateLock)
            {
                if (!_status.IsRunning) return;
                _status.State   = "Stopped";
                _status.IsRunning = false;
                _cts?.Cancel();
                _pauseEvent.Set();
            }
            Log.Information("Backfill stopped. Total points written: {Points}", _status.TotalPoints);
        }

        /// <summary>
        /// Run the full backfill on the current thread (blocking).
        /// </summary>
        public void Run(CancellationToken ct)
        {
            var totalDuration = _config.EndDate - _config.StartDate;
            var totalChunks = (int)Math.Ceiling(totalDuration.TotalDays / _config.ChunkDays);

            DateTime windowStart = _config.StartDate;
            int chunkIndex = 0;

            while (windowStart < _config.EndDate && !ct.IsCancellationRequested)
            {
                _pauseEvent.Wait(ct); // blocks if paused

                if (ct.IsCancellationRequested) break;

                DateTime windowEnd = windowStart.AddDays(_config.ChunkDays);
                if (windowEnd > _config.EndDate) windowEnd = _config.EndDate;

                lock (_stateLock)
                {
                    _status.CurrentTime = windowStart;
                    _status.State       = $"Processing chunk {chunkIndex + 1}/{totalChunks}";
                    _status.ProgressPct = Math.Min(100, windowStart.Subtract(_config.StartDate).TotalMilliseconds
                        / Math.Max(1, totalDuration.TotalMilliseconds) * 100);
                }

                Log.Information("[Backfill] Chunk {N}/{Total}: {Start:d} → {End:d}",
                    chunkIndex + 1, totalChunks, windowStart, windowEnd);

                // Read historical data for all tags in this time window
                var results = _reader.ReadRaw(_tags, windowStart, windowEnd, _config.MaxPointsPerCall);

                int chunkPoints = 0;
                foreach (var tagResult in results)
                {
                    if (tagResult.Error != null)
                    {
                        Log.Warning("[Backfill] Tag {Tag} error: {Error}", tagResult.TagName, tagResult.Error);
                        continue;
                    }

                    if (tagResult.Count == 0) continue;

                    var points = tagResult.Points
                        .Where(p => p.IsGood)
                        .Select(p => ConvertToTsdbPoint(tagResult.TagName, p))
                        .ToList();

                    if (points.Count == 0) continue;

                    try
                    {
                        int inserted = _tsdb.BulkUpsertFallback(points, _config.BatchSize);
                        chunkPoints += inserted;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "[Backfill] Bulk insert failed for tag {Tag}, trying fallback", tagResult.TagName);
                        try
                        {
                            int inserted = _tsdb.BulkUpsertFallback(points, 50);
                            chunkPoints += inserted;
                        }
                        catch (Exception ex2)
                        {
                            Log.Error(ex2, "[Backfill] All insert methods failed for tag {Tag}", tagResult.TagName);
                        }
                    }
                }

                lock (_stateLock)
                {
                    _status.TotalPoints += chunkPoints;
                    _status.TagsProcessed++;
                }

                Log.Information("[Backfill] Chunk {N} complete: {Points} points written",
                    chunkIndex + 1, chunkPoints);

                // Brief pause between chunks to avoid overwhelming the HDA server
                if (_config.PauseBetweenChunksMs > 0)
                    Thread.Sleep(_config.PauseBetweenChunksMs);

                windowStart = windowEnd;
                chunkIndex++;
            }

            lock (_stateLock)
            {
                _status.IsRunning   = false;
                _status.EndTime     = DateTime.UtcNow;
                _status.State       = ct.IsCancellationRequested ? "Cancelled" : "Completed";
                _status.ProgressPct = 100;
                _status.Elapsed     = _status.EndTime.Value - _status.StartTime.Value;
            }

            Log.Information("[Backfill] Finished. Total: {Points} points in {Elapsed}",
                _status.TotalPoints, _status.Elapsed);
        }

        private void RunBackfill(CancellationToken ct)
        {
            try
            {
                Run(ct);
            }
            catch (OperationCanceledException)
            {
                Log.Information("Backfill cancelled");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Backfill failed with unhandled exception");
                lock (_stateLock)
                {
                    _status.IsRunning = false;
                    _status.State     = "Error: " + ex.Message;
                }
            }
        }

        private TsdbDataPoint ConvertToTsdbPoint(string tagName, TimeSeriesPoint pt)
        {
            double? numericValue = null;
            string textValue = null;
            string valueType = "unknown";

            if (pt.Value != null)
            {
                if (pt.Value is double d)       { numericValue = d;   valueType = "double"; }
                else if (pt.Value is float f)   { numericValue = f;   valueType = "float"; }
                else if (pt.Value is int i)     { numericValue = i;   valueType = "int"; }
                else if (pt.Value is long l)    { numericValue = l;   valueType = "long"; }
                else if (pt.Value is short s)   { numericValue = s;   valueType = "short"; }
                else if (pt.Value is decimal dc){ numericValue = (double)dc; valueType = "decimal"; }
                else
                {
                    if (double.TryParse(pt.Value.ToString(), out var parsed))
                    {
                        numericValue = parsed;
                        valueType = "parsed";
                    }
                    else
                    {
                        textValue = pt.Value.ToString();
                        valueType = "string";
                    }
                }
            }

            return new TsdbDataPoint
            {
                Timestamp  = DateTime.SpecifyKind(pt.Timestamp, DateTimeKind.Utc),
                Tag        = tagName,
                Value      = numericValue,
                ValueText  = textValue,
                Quality    = pt.Quality,
                BrokerId   = _config.BrokerId,
                ValueType  = valueType
            };
        }

        private BackfillStatus CloneStatus()
        {
            return new BackfillStatus
            {
                IsRunning          = _status.IsRunning,
                IsPaused           = _status.IsPaused,
                StartTime          = _status.StartTime,
                EndTime            = _status.EndTime,
                CurrentTime        = _status.CurrentTime,
                TotalPoints        = _status.TotalPoints,
                TagsProcessed      = _status.TagsProcessed,
                TotalTags          = _status.TotalTags > 0 ? _status.TotalTags : _tags.Count,
                State              = _status.State,
                Elapsed            = _status.Elapsed,
                ProgressPct        = _status.ProgressPct,
                EstimatedRemaining = EstimateRemaining()
            };
        }

        private string EstimateRemaining()
        {
            if (!_status.IsRunning || _status.TotalPoints == 0 || _status.Elapsed.TotalSeconds < 1)
                return "Unknown";

            var pointsPerSecond = _status.TotalPoints / _status.Elapsed.TotalSeconds;
            if (pointsPerSecond < 1) return "Calculating...";

            var remainingChunks = ((_config.EndDate - (_status.CurrentTime ?? _config.StartDate)).TotalDays / _config.ChunkDays);
            var remainingSeconds = (remainingChunks * _config.ChunkDays * 24 * 3600) / pointsPerSecond;

            return TimeSpan.FromSeconds(remainingSeconds).ToString(@"d\.hh\:mm\:ss");
        }
    }
}
