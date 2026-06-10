// ═══════════════════════════════════════════════════════════════════════════
// BROKER ENGINE — Central Orchestrator
// ───────────────────────────────────────────────────────────────────────────
// Owns the OPC HDA connection, STA dispatcher, browser, reader, and cache.
// Provides async methods that API controllers call.
// All COM interop is dispatched to the STA thread automatically.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Configuration;
using System.Threading;
using System.Threading.Tasks;
using OpcHdaBroker.Api.Controllers;
using OpcHdaBroker.Api.Models;
using OpcHdaBroker.ComInterop;
using OpcHdaBroker.TimescaleDb;
using Serilog;

namespace OpcHdaBroker
{
    /// <summary>
    /// Singleton orchestrator. Initialized once at startup, used by all controllers.
    /// </summary>
    public static class BrokerEngine
    {
        private static readonly ILogger Log = Serilog.Log.ForContext(typeof(BrokerEngine));

        // ── Core components ──────────────────────────────────────────────
        private static StaThreadDispatcher _dispatcher;
        private static HdaConnection       _connection;
        private static HdaBrowser           _browser;
        private static HdaReader            _reader;
        private static DateTime             _startedAt;
        private static List<string>         _discoveredTags;

        // ── TimescaleDB components ────────────────────────────────────────
        private static TsdbRepository      _tsdb;
        private static BackfillService      _backfill;
        private static BackfillConfig       _backfillConfig;

        // ── Public accessors ─────────────────────────────────────────────
        public static Cache.MemoryCache Cache { get; } = new Cache.MemoryCache();

        public static TimeSpan GetUptime() => DateTime.UtcNow - _startedAt;

        /// <summary>
        /// Initialize all components and connect to KepServerEX.
        /// Called once from Program.cs at startup.
        /// </summary>
        public static void Initialize()
        {
            _startedAt = DateTime.UtcNow;

            string primaryUrl  = ConfigurationManager.AppSettings["Hda.PrimaryUrl"]
                                 ?? "opchda://localhost/Kepware.KEPServerEX_HDA.V6";
            string fallbackUrl = ConfigurationManager.AppSettings["Hda.FallbackUrl"]
                                 ?? "opchda://127.0.0.1/Kepware.KEPServerEX_HDA.V6";

            Log.Information("═══════════════════════════════════════════════════");
            Log.Information("  OPC HDA Broker — Starting");
            Log.Information("  Primary URL  : {Url}", primaryUrl);
            Log.Information("  Fallback URL : {Url}", fallbackUrl);
            Log.Information("═══════════════════════════════════════════════════");

            // 1. Create STA thread for COM calls
            _dispatcher = new StaThreadDispatcher();

            // 2. Create connection and connect on STA thread
            _connection = new HdaConnection(primaryUrl, fallbackUrl);
            _dispatcher.InvokeAsync(() => _connection.Connect()).Wait();

            // 3. Create browser and reader (they use the connection)
            _browser = new HdaBrowser(_connection);
            _reader  = new HdaReader(_connection);

            // 4. Pre-warm the tag cache
            _discoveredTags = _dispatcher.InvokeAsync(() => _browser.DiscoverAllTags()).Result;
            Cache.GetOrAdd("tags", () => _discoveredTags, TimeSpan.FromSeconds(60));

            Log.Information("Broker engine initialized — {TagCount} tags discovered", _discoveredTags.Count);

            // 5. Initialize TimescaleDB
            InitializeTimescaleDb();
        }

        /// <summary>
        /// Initialize TimescaleDB repository and optionally start backfill.
        /// </summary>
        private static void InitializeTimescaleDb()
        {
            string connString = ConfigurationManager.AppSettings["Tsdb.ConnectionString"];
            string brokerId   = ConfigurationManager.AppSettings["Ingestion.BrokerId"] ?? "kepserver01";

            if (string.IsNullOrWhiteSpace(connString))
            {
                Log.Warning("TimescaleDB connection string not configured — TSDB features disabled");
                return;
            }

            try
            {
                _tsdb = new TsdbRepository(connString, brokerId);

                if (!_tsdb.TestConnection())
                {
                    Log.Error("TimescaleDB connection failed — check connection string");
                    return;
                }

                _tsdb.EnsureSchema();
                Log.Information("TimescaleDB connected. Rows in DB: {Count}, Tags: {Tags}",
                    _tsdb.GetRowCount(), _tsdb.GetTagCount());

                // Load backfill config
                _backfillConfig = BackfillConfig.FromAppSettings();
                _backfill = new BackfillService(_tsdb, _backfillConfig, _reader, _discoveredTags);

                // Auto-start backfill if configured
                if (_backfillConfig.AutoStart && _backfillConfig.Enabled)
                {
                    Log.Information("Auto-starting backfill...");
                    _backfill.StartAsync();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to initialize TimescaleDB");
            }
        }

        /// <summary>
        /// Shutdown: disconnect and dispose all resources.
        /// </summary>
        public static void Shutdown()
        {
            Log.Information("Broker engine shutting down...");
            try
            {
                _backfill?.Stop();
                _tsdb?.Dispose();
                _dispatcher?.InvokeAsync(() => _connection?.Disconnect()).Wait(TimeSpan.FromSeconds(5));
            }
            catch { /* best effort */ }

            _connection?.Dispose();
            _dispatcher?.Dispose();
            Log.Information("Broker engine stopped.");
        }

        // ══════════════════════════════════════════════════════════════════
        // ASYNC METHODS (called by API controllers)
        // All COM work is dispatched to the STA thread.
        // ══════════════════════════════════════════════════════════════════

        public static Task<List<string>> GetTagsAsync()
        {
            int ttlSec = int.TryParse(ConfigurationManager.AppSettings["Cache.TagListTtlSec"], out int v) ? v : 60;

            return _dispatcher.InvokeAsync(() =>
                Cache.GetOrAdd("tags", () => _browser.DiscoverAllTags(), TimeSpan.FromSeconds(ttlSec))
            );
        }

        /// <summary>
        /// Add tags dynamically and persist them to tags.txt.
        /// Returns the number of new tags actually added.
        /// </summary>
        public static async Task<int> AddTagsAsync(List<string> newTags)
        {
            var currentTags = await GetTagsAsync();
            int before = currentTags.Count;

            await _dispatcher.InvokeAsync(() =>
            {
                _browser.AddTags(currentTags, newTags);
                _browser.SaveTagsToFile(currentTags);
            });

            // Update the cache with the modified list
            Cache.Invalidate("tags");
            Cache.GetOrAdd("tags", () => currentTags, TimeSpan.FromSeconds(600));

            return currentTags.Count - before;
        }

        public static Task<List<TagReadResult>> ReadRawAsync(
            IList<string> tags, DateTime from, DateTime to, int maxValues)
        {
            EnsureConnected();
            return _dispatcher.InvokeAsync(() => _reader.ReadRaw(tags, from, to, maxValues));
        }

        public static Task<List<TagReadResult>> ReadLatestAsync(
            IList<string> tags, int lookbackMinutes)
        {
            EnsureConnected();
            return _dispatcher.InvokeAsync(() => _reader.ReadLatest(tags, lookbackMinutes));
        }

        public static Task<List<TagReadResult>> ReadProcessedAsync(
            IList<string> tags, DateTime from, DateTime to, int aggregateId, TimeSpan interval)
        {
            EnsureConnected();
            return _dispatcher.InvokeAsync(() => _reader.ReadProcessed(tags, from, to, aggregateId, interval));
        }

        public static Task<Dictionary<int, string>> GetAggregatesAsync()
        {
            return _dispatcher.InvokeAsync(() =>
                Cache.GetOrAdd("aggregates", () => _reader.GetSupportedAggregates(), TimeSpan.FromMinutes(10))
            );
        }

        public static async Task<StatusDtoExt> GetStatusAsync()
        {
            var tags = await GetTagsAsync();

            // Get server status via SDK's GetServerStatus()
            HistorianStatus status = null;
            try
            {
                status = await _dispatcher.InvokeAsync(() => _connection.GetStatus());
            }
            catch { /* graceful fallback */ }

            return new StatusDtoExt
            {
                Connected       = _connection.IsConnected,
                ServerStatus    = status?.Status ?? (_connection.IsConnected ? "Connected" : "Disconnected"),
                ServerVersion   = status?.ProductVersion ?? "N/A",
                VendorInfo      = status?.VendorInfo ?? "KepServerEX 6",
                TagCount        = tags.Count,
                BrokerUptime    = GetUptime().ToString(@"d\.hh\:mm\:ss"),
                BrokerStartedAt = _startedAt,
                SupportedAggregates = await GetAggregatesAsync()
            };
        }

        public static Task<Diagnostics.DiagnosticReport> RunDiagnosticsAsync()
        {
            return _dispatcher.InvokeAsync(() =>
            {
                var runner = new Diagnostics.DiagnosticRunner(_connection);
                return runner.RunAll();
            });
        }

        // ══════════════════════════════════════════════════════════════════
        // TIMESACALEDB / BACKFILL METHODS
        // ══════════════════════════════════════════════════════════════════

        public static bool IsTimescaleDbEnabled => _tsdb != null;

        /// <summary>
        /// Test TimescaleDB connection.
        /// </summary>
        public static TsdbStatusDto GetTimescaleDbStatus()
        {
            if (_tsdb == null)
                return new TsdbStatusDto { Connected = false, Message = "Not configured" };

            BackfillStatusDto backfillDto = null;
            var backfillStatus = _backfill?.Status;
            if (backfillStatus != null)
            {
                backfillDto = new BackfillStatusDto
                {
                    IsRunning           = backfillStatus.IsRunning,
                    IsPaused            = backfillStatus.IsPaused,
                    StartTime           = backfillStatus.StartTime,
                    EndTime             = backfillStatus.EndTime,
                    CurrentTime         = backfillStatus.CurrentTime,
                    TotalPoints         = backfillStatus.TotalPoints,
                    TagsProcessed       = backfillStatus.TagsProcessed,
                    TotalTags           = backfillStatus.TotalTags > 0 ? backfillStatus.TotalTags : 0,
                    State               = backfillStatus.State,
                    ProgressPct         = backfillStatus.ProgressPct,
                    EstimatedRemaining  = backfillStatus.EstimatedRemaining,
                    Elapsed             = backfillStatus.Elapsed.ToString(@"d\.hh\:mm\:ss")
                };
            }

            return new TsdbStatusDto
            {
                Connected      = _tsdb.TestConnection(),
                RowCount      = _tsdb.GetRowCount(),
                TagCount      = _tsdb.GetTagCount(),
                OldestTime    = _tsdb.GetOldestTimestamp(),
                NewestTime    = _tsdb.GetNewestTimestamp(),
                Backfill      = backfillDto,
                Message       = "Connected"
            };
        }

        /// <summary>
        /// Start the backfill.
        /// </summary>
        public static void StartBackfill()
        {
            if (_backfill == null)
            {
                Log.Warning("Backfill not available — TimescaleDB not configured");
                return;
            }
            _backfill.StartAsync();
        }

        /// <summary>
        /// Pause the backfill.
        /// </summary>
        public static void PauseBackfill()
        {
            _backfill?.Pause();
        }

        /// <summary>
        /// Resume the backfill.
        /// </summary>
        public static void ResumeBackfill()
        {
            _backfill?.Resume();
        }

        /// <summary>
        /// Stop the backfill.
        /// </summary>
        public static void StopBackfill()
        {
            _backfill?.Stop();
        }

        /// <summary>
        /// Run backfill on a custom time range (blocking call).
        /// </summary>
        public static void RunBackfillRange(DateTime startDate, DateTime endDate)
        {
            if (_tsdb == null)
            {
                Log.Warning("TimescaleDB not configured");
                return;
            }

            var customConfig = BackfillConfig.FromAppSettings();
            customConfig.StartDate = startDate.ToUniversalTime();
            customConfig.EndDate   = endDate.ToUniversalTime();

            var customBackfill = new BackfillService(_tsdb, customConfig, _reader, _discoveredTags);
            customBackfill.Run(CancellationToken.None);
        }

        /// <summary>
        /// Truncate all data from TimescaleDB (dangerous).
        /// </summary>
        public static void TruncateTimescaleDb()
        {
            if (_tsdb == null)
            {
                Log.Warning("TimescaleDB not configured");
                return;
            }
            _tsdb.TruncateData();
        }

        private static void EnsureConnected()
        {
            if (!_connection.IsConnected)
            {
                Log.Warning("Connection lost — attempting reconnect on next COM call");
                _dispatcher.InvokeAsync(() => _connection.Reconnect()).Wait();
            }
        }
    }
}
