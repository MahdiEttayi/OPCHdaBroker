// ═══════════════════════════════════════════════════════════════════════════
// PROGRAM ENTRY POINT
// ───────────────────────────────────────────────────────────────────────────
// Runs as either:
//   1. Console app (for development/debugging) — just press Enter to stop
//   2. Windows Service (for production) — installed via sc.exe
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Configuration;
using System.ServiceProcess;
using Microsoft.Owin.Hosting;
using Serilog;

namespace OpcHdaBroker
{
    class Program
    {
        static void Main(string[] args)
        {
            // ── Configure Serilog ────────────────────────────────────────
            string logPath = ConfigurationManager.AppSettings["Log.FilePath"] ?? "logs/broker-.log";
            string logLevel = ConfigurationManager.AppSettings["Log.Level"] ?? "Information";

            var logConfig = new LoggerConfiguration()
                .MinimumLevel.Is(ParseLogLevel(logLevel))
                .WriteTo.Console(
                    outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                .WriteTo.File(logPath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");

            Log.Logger = logConfig.CreateLogger();

            // ── Detect run mode ──────────────────────────────────────────
            bool isService = !Environment.UserInteractive;

            if (isService)
            {
                // Running as Windows Service
                ServiceBase.Run(new BrokerWindowsService());
            }
            else
            {
                // Running as console app (development mode)
                RunAsConsole();
            }

            Log.CloseAndFlush();
        }

        static void RunAsConsole()
        {
            string baseUrl = ConfigurationManager.AppSettings["Api.BaseUrl"] ?? "http://localhost:5000";

            Console.WriteLine();
            Console.WriteLine("  ╔═══════════════════════════════════════════════════╗");
            Console.WriteLine("  ║  OPC HDA Broker — Console Mode                   ║");
            Console.WriteLine("  ╚═══════════════════════════════════════════════════╝");
            Console.WriteLine();

            try
            {
                // 1. Initialize the broker engine (connect to KepServerEX)
                BrokerEngine.Initialize();

                // 2. Start the OWIN self-hosted Web API
                using (WebApp.Start<Api.Startup>(baseUrl))
                {
                    Log.Information("REST API listening on {BaseUrl}", baseUrl);
                    Console.WriteLine();
                    Console.WriteLine($"  ✓  API ready at {baseUrl}");
                    Console.WriteLine($"  ✓  Swagger UI at {baseUrl}/swagger");
                    Console.WriteLine();
                    Console.WriteLine("  Endpoints:");
                    Console.WriteLine($"    GET {baseUrl}/api/tags");
                    Console.WriteLine($"    GET {baseUrl}/api/read/raw?tags=...&from=...&to=...");
                    Console.WriteLine($"    GET {baseUrl}/api/read/latest?tags=...");
                    Console.WriteLine($"    GET {baseUrl}/api/read/processed?tags=...&aggregate=average&intervalSec=3600");
                    Console.WriteLine($"    GET {baseUrl}/api/read/aggregates");
                    Console.WriteLine($"    GET {baseUrl}/api/status");
                    Console.WriteLine($"    GET {baseUrl}/api/health");
                    Console.WriteLine();
                    Console.WriteLine("  Press Enter to stop...");
                    Console.ReadLine();
                }
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Broker failed to start");
                Console.WriteLine($"\n  [FATAL] {ex.Message}");
                Console.WriteLine("  Press Enter to exit...");
                Console.ReadLine();
            }
            finally
            {
                BrokerEngine.Shutdown();
            }
        }

        static Serilog.Events.LogEventLevel ParseLogLevel(string level)
        {
            switch (level?.ToLowerInvariant())
            {
                case "verbose":     return Serilog.Events.LogEventLevel.Verbose;
                case "debug":       return Serilog.Events.LogEventLevel.Debug;
                case "warning":     return Serilog.Events.LogEventLevel.Warning;
                case "error":       return Serilog.Events.LogEventLevel.Error;
                case "fatal":       return Serilog.Events.LogEventLevel.Fatal;
                default:            return Serilog.Events.LogEventLevel.Information;
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // WINDOWS SERVICE WRAPPER
    // ════════════════════════════════════════════════════════════════════

    class BrokerWindowsService : ServiceBase
    {
        private IDisposable _webApp;

        public BrokerWindowsService()
        {
            ServiceName = "OpcHdaBroker";
        }

        protected override void OnStart(string[] args)
        {
            Log.Information("Windows Service starting...");
            string baseUrl = ConfigurationManager.AppSettings["Api.BaseUrl"] ?? "http://+:5000";

            BrokerEngine.Initialize();
            _webApp = WebApp.Start<Api.Startup>(baseUrl);

            Log.Information("Windows Service started — API on {Url}", baseUrl);
        }

        protected override void OnStop()
        {
            Log.Information("Windows Service stopping...");
            _webApp?.Dispose();
            BrokerEngine.Shutdown();
            Log.Information("Windows Service stopped.");
        }
    }
}
