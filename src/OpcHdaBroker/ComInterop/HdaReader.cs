// ═══════════════════════════════════════════════════════════════════════════
// HDA READER
// ───────────────────────────────────────────────────────────────────────────
// Wraps OPC HDA ReadRaw and ReadProcessed calls into clean C# methods.
// Returns structured results that the REST API can serialize to JSON.
// Must be called from the STA thread via StaThreadDispatcher.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Linq;
using OpcClientSdk;
using OpcClientSdk.Hda;
using OpcClientSdk.Da;
using Serilog;

namespace OpcHdaBroker.ComInterop
{
    /// <summary>
    /// A single time-series data point returned from the historian.
    /// </summary>
    public class TimeSeriesPoint
    {
        public DateTime  Timestamp { get; set; }
        public object    Value     { get; set; }
        public string    Quality   { get; set; }
        public bool      IsGood    { get; set; }
    }

    /// <summary>
    /// Result set for a single tag's historical query.
    /// </summary>
    public class TagReadResult
    {
        public string                 TagName { get; set; }
        public List<TimeSeriesPoint>  Points  { get; set; } = new List<TimeSeriesPoint>();
        public int                    Count   => Points.Count;
        public string                 Error   { get; set; }
    }

    /// <summary>
    /// Reads historical data from KepServerEX via OPC HDA TsCHdaTrend.
    /// </summary>
    public class HdaReader
    {
        private static readonly ILogger Log = Serilog.Log.ForContext<HdaReader>();

        private readonly HdaConnection _connection;

        public HdaReader(HdaConnection connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        }

        /// <summary>
        /// Read raw historical values for the specified tags within a time range.
        /// This calls TsCHdaTrend.ReadRaw() — returns unsummarized samples.
        /// Must be called on the STA thread.
        /// </summary>
        public List<TagReadResult> ReadRaw(
            IList<string> tagPaths,
            DateTime      startTime,
            DateTime      endTime,
            int           maxValues = 10000)
        {
            var results = new List<TagReadResult>();

            try
            {
                var trend = new TsCHdaTrend(_connection.Server)
                {
                    StartTime     = new TsCHdaTime(startTime.ToUniversalTime()),
                    EndTime       = new TsCHdaTime(endTime.ToUniversalTime()),
                    IncludeBounds = true,
                    MaxValues     = maxValues,
                };

                TsCHdaItem[] hdaItems = tagPaths
                    .Select(t => trend.AddItem(new OpcItem(t)))
                    .ToArray();

                Log.Debug("ReadRaw: {Count} tags, {Start} → {End}, max {Max}",
                    tagPaths.Count, startTime, endTime, maxValues);

                TsCHdaItemValueCollection[] collections = trend.ReadRaw(hdaItems);

                foreach (var col in collections)
                {
                    var result = new TagReadResult { TagName = col.ItemName };

                    foreach (TsCHdaItemValue val in col)
                    {
                        bool isGood = ((int)val.Quality.GetCode() &
                                       (int)TsDaQualityMasks.QualityMask) ==
                                      (int)TsDaQualityBits.Good;

                        // OPC HDA spec: timestamps are UTC. The SDK returns Kind=Unspecified.
                        // Do NOT call .ToUniversalTime() — that assumes the value is local
                        // and subtracts 1 hour (UTC+1 host), creating a 1h drift.
                        // Just stamp it as UTC directly.
                        result.Points.Add(new TimeSeriesPoint
                        {
                            Timestamp = DateTime.SpecifyKind(val.Timestamp, DateTimeKind.Utc),
                            Value     = isGood ? val.Value : null,
                            Quality   = val.Quality.ToString(),
                            IsGood    = isGood
                        });
                    }

                    results.Add(result);
                }

                Log.Debug("ReadRaw complete: {TotalPoints} total points across {Tags} tags",
                    results.Sum(r => r.Count), results.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ReadRaw failed for {Count} tags", tagPaths.Count);
                // Return error results for all tags
                foreach (var tag in tagPaths)
                {
                    results.Add(new TagReadResult { TagName = tag, Error = ex.Message });
                }
            }

            return results;
        }

        /// <summary>
        /// Read the latest (most recent) value for each tag.
        /// Implemented as ReadRaw with a short lookback and maxValues=1.
        /// Must be called on the STA thread.
        /// </summary>
        public List<TagReadResult> ReadLatest(IList<string> tagPaths, int lookbackMinutes = 60)
        {
            // Always use UTC so the lookback window is correct regardless of host timezone.
            var endTime   = DateTime.UtcNow;
            var startTime = endTime.AddMinutes(-lookbackMinutes);

            // Read with maxValues=1 → only the most recent value
            return ReadRaw(tagPaths, startTime, endTime, maxValues: 1);
        }

        /// <summary>
        /// Read processed (aggregated) values using OPC HDA server-side aggregation.
        /// The historian computes the aggregate directly on TSD files — much faster
        /// than reading raw points and computing in code.
        ///
        /// Supported aggregates are defined by the OPC HDA server (KepServerEX).
        /// Common ones: Average(2), Minimum(4), Maximum(5), Count(7), etc.
        /// Must be called on the STA thread.
        /// </summary>
        public List<TagReadResult> ReadProcessed(
            IList<string> tagPaths,
            DateTime      startTime,
            DateTime      endTime,
            int           aggregateId,
            TimeSpan      resampleInterval)
        {
            var results = new List<TagReadResult>();

            try
            {
                var trend = new TsCHdaTrend(_connection.Server)
                {
                    StartTime   = new TsCHdaTime(startTime.ToUniversalTime()),
                    EndTime     = new TsCHdaTime(endTime.ToUniversalTime()),
                    Aggregate   = aggregateId,
                    ResampleInterval = (decimal)resampleInterval.TotalSeconds,
                };

                TsCHdaItem[] hdaItems = tagPaths
                    .Select(t => trend.AddItem(new OpcItem(t)))
                    .ToArray();

                Log.Debug("ReadProcessed: {Count} tags, aggregate={Agg}, interval={Interval}s",
                    tagPaths.Count, aggregateId, resampleInterval.TotalSeconds);

                TsCHdaItemValueCollection[] collections = trend.ReadProcessed(hdaItems);

                foreach (var col in collections)
                {
                    var result = new TagReadResult { TagName = col.ItemName };

                    foreach (TsCHdaItemValue val in col)
                    {
                        bool isGood = ((int)val.Quality.GetCode() &
                                       (int)TsDaQualityMasks.QualityMask) ==
                                      (int)TsDaQualityBits.Good;

                        // OPC HDA spec: timestamps are UTC. See ReadRaw comment.
                        result.Points.Add(new TimeSeriesPoint
                        {
                            Timestamp = DateTime.SpecifyKind(val.Timestamp, DateTimeKind.Utc),
                            Value     = isGood ? val.Value : null,
                            Quality   = val.Quality.ToString(),
                            IsGood    = isGood
                        });
                    }

                    results.Add(result);
                }

                Log.Debug("ReadProcessed complete: {TotalPoints} aggregate points",
                    results.Sum(r => r.Count));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ReadProcessed failed");
                foreach (var tag in tagPaths)
                    results.Add(new TagReadResult { TagName = tag, Error = ex.Message });
            }

            return results;
        }

        /// <summary>
        /// Query the server for supported aggregate functions.
        /// Uses the SDK's built-in GetAggregates() — no raw COM QI needed.
        /// Returns a dictionary of aggregate ID → name.
        /// Must be called on the COM thread.
        /// </summary>
        public Dictionary<int, string> GetSupportedAggregates()
        {
            var aggregates = new Dictionary<int, string>();

            try
            {
                TsCHdaAggregate[] sdkAggs = _connection.Server.GetAggregates();

                if (sdkAggs != null && sdkAggs.Length > 0)
                {
                    foreach (var agg in sdkAggs)
                    {
                        aggregates[agg.Id] = agg.Name;
                    }

                    Log.Information("Server supports {Count} aggregate(s): {Names}",
                        aggregates.Count, string.Join(", ", aggregates.Values));
                }
                else
                {
                    Log.Warning("GetAggregates returned null/empty — using defaults");
                    AddDefaultAggregates(aggregates);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "GetAggregates failed — using defaults");
                AddDefaultAggregates(aggregates);
            }

            return aggregates;
        }

        private static void AddDefaultAggregates(Dictionary<int, string> aggregates)
        {
            aggregates[1]  = "Interpolative";
            aggregates[2]  = "Average";
            aggregates[4]  = "Minimum";
            aggregates[5]  = "Maximum";
            aggregates[7]  = "Count";
            aggregates[13] = "StdDev";
            aggregates[17] = "Range";
        }
    }
}

