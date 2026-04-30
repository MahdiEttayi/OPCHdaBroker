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
using System.Threading.Tasks;
using OpcHdaBroker.Api.Controllers;
using OpcHdaBroker.ComInterop;
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
            var tags = _dispatcher.InvokeAsync(() => _browser.DiscoverAllTags()).Result;
            Cache.GetOrAdd("tags", () => tags, TimeSpan.FromSeconds(60));

            Log.Information("Broker engine initialized — {TagCount} tags discovered", tags.Count);
        }

        /// <summary>
        /// Shutdown: disconnect and dispose all resources.
        /// </summary>
        public static void Shutdown()
        {
            Log.Information("Broker engine shutting down...");
            try
            {
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
