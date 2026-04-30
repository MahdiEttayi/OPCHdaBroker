// ═══════════════════════════════════════════════════════════════════════════
// DIAGNOSTIC RUNNER
// ───────────────────────────────────────────────────────────────────────────
// Comprehensive diagnostic tool that explores every available SDK method
// for tag discovery and data reading. Logs all results to help determine
// the correct approach for this specific KepServerEX configuration.
//
// Run via: GET /api/diagnostics
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using OpcClientSdk;
using OpcClientSdk.Hda;
using Serilog;

namespace OpcHdaBroker.Diagnostics
{
    public class DiagnosticRunner
    {
        private static readonly ILogger Log = Serilog.Log.ForContext<DiagnosticRunner>();

        private readonly ComInterop.HdaConnection _connection;
        private readonly List<string> _results = new List<string>();

        public DiagnosticRunner(ComInterop.HdaConnection connection)
        {
            _connection = connection;
        }

        /// <summary>
        /// Run all diagnostics and return a structured report.
        /// Must be called on the COM thread.
        /// </summary>
        public DiagnosticReport RunAll()
        {
            var report = new DiagnosticReport();
            _results.Clear();

            try
            {
                // ── 1. Dump SDK API surface ──────────────────────────────
                DumpSdkApi(report);

                // ── 2. Try SDK's built-in GetStatus ──────────────────────
                TryGetStatus(report);

                // ── 3. Try SDK's CreateBrowser ───────────────────────────
                TryCreateBrowser(report);

                // ── 4. Try SDK's Browse method directly ──────────────────
                TryBrowseDirect(report);

                // ── 5. Try different tag path formats with ReadRaw ───────
                TryReadWithDifferentPaths(report);

                // ── 6. Try raw COM QI with detailed HRESULT analysis ─────
                TryRawComQI(report);

                // ── 7. Check COM threading apartment ─────────────────────
                CheckThreading(report);
            }
            catch (Exception ex)
            {
                report.FatalError = ex.ToString();
            }

            report.Steps = _results;
            return report;
        }

        private void DumpSdkApi(DiagnosticReport report)
        {
            AddResult("═══ SDK API SURFACE ═══");

            var server = _connection.Server;
            Type t = server.GetType();
            var methods = new List<string>();

            while (t != null && t != typeof(object))
            {
                var declared = t.GetMethods(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

                foreach (var m in declared.OrderBy(m => m.Name))
                {
                    if (m.IsSpecialName) continue; // skip get_/set_
                    var parms = string.Join(", ",
                        m.GetParameters().Select(p =>
                            $"{(p.IsOut ? "out " : "")}{p.ParameterType.Name} {p.Name}"));
                    methods.Add($"  [{t.Name}] {m.ReturnType.Name} {m.Name}({parms})");
                }
                t = t.BaseType;
            }

            report.SdkMethods = methods;
            foreach (var m in methods)
                AddResult(m);

            // Also dump properties
            t = server.GetType();
            var props = new List<string>();
            while (t != null && t != typeof(object))
            {
                var declared = t.GetProperties(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                foreach (var p in declared)
                    props.Add($"  [{t.Name}] {p.PropertyType.Name} {p.Name}");
                t = t.BaseType;
            }
            report.SdkProperties = props;
        }

        private void TryGetStatus(DiagnosticReport report)
        {
            AddResult("═══ SDK GetStatus() ═══");
            try
            {
                // Try calling GetStatus via reflection (it may exist on the base type)
                var server = _connection.Server;
                var method = server.GetType().GetMethod("GetStatus",
                    BindingFlags.Public | BindingFlags.Instance);

                if (method != null)
                {
                    AddResult($"  GetStatus found on {method.DeclaringType.Name}");
                    var result = method.Invoke(server, null);
                    if (result != null)
                    {
                        AddResult($"  Result type: {result.GetType().FullName}");
                        // Dump all properties of the result
                        foreach (var prop in result.GetType().GetProperties())
                        {
                            try
                            {
                                var val = prop.GetValue(result);
                                AddResult($"  {prop.Name} = {val}");
                            }
                            catch { }
                        }
                        report.ServerStatus = result.ToString();
                        report.GetStatusWorked = true;
                    }
                }
                else
                {
                    AddResult("  GetStatus() NOT FOUND on TsCHdaServer");
                }
            }
            catch (Exception ex)
            {
                AddResult($"  GetStatus FAILED: {ex.InnerException?.Message ?? ex.Message}");
                report.GetStatusWorked = false;
            }
        }

        private void TryCreateBrowser(DiagnosticReport report)
        {
            AddResult("═══ SDK CreateBrowser() ═══");
            try
            {
                var server = _connection.Server;

                // Try CreateBrowser with various signatures
                var methods = server.GetType().GetMethods()
                    .Where(m => m.Name.Contains("Browse") || m.Name.Contains("browser") ||
                                m.Name.Contains("Browser"))
                    .ToList();

                AddResult($"  Found {methods.Count} browse-related method(s):");
                foreach (var m in methods)
                {
                    var parms = string.Join(", ",
                        m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                    AddResult($"    {m.ReturnType.Name} {m.Name}({parms})");
                }

                // Try CreateBrowser() with no args
                var createBrowser = server.GetType().GetMethod("CreateBrowser",
                    new Type[0]);
                if (createBrowser != null)
                {
                    AddResult("  Calling CreateBrowser()...");
                    var browser = createBrowser.Invoke(server, null);
                    if (browser != null)
                    {
                        AddResult($"  SUCCESS! Browser type: {browser.GetType().FullName}");
                        report.CreateBrowserWorked = true;
                        ExplorebrowserObject(browser, report);
                    }
                }

                // Try CreateBrowser(OpcBrowseFilters)
                var createBrowser2 = server.GetType().GetMethods()
                    .FirstOrDefault(m => m.Name == "CreateBrowser" && m.GetParameters().Length > 0);
                if (createBrowser2 != null && !report.CreateBrowserWorked)
                {
                    AddResult($"  Trying CreateBrowser with params...");
                    try
                    {
                        var pTypes = createBrowser2.GetParameters();
                        var args = new object[pTypes.Length];
                        for (int i = 0; i < pTypes.Length; i++)
                        {
                            if (pTypes[i].ParameterType.IsValueType)
                                args[i] = Activator.CreateInstance(pTypes[i].ParameterType);
                            else
                                args[i] = null;
                        }
                        var browser = createBrowser2.Invoke(server, args);
                        if (browser != null)
                        {
                            AddResult($"  SUCCESS! Browser type: {browser.GetType().FullName}");
                            report.CreateBrowserWorked = true;
                            ExplorebrowserObject(browser, report);
                        }
                    }
                    catch (Exception ex)
                    {
                        AddResult($"  CreateBrowser with params FAILED: {ex.InnerException?.Message ?? ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                AddResult($"  CreateBrowser FAILED: {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        private void ExplorebrowserObject(object browser, DiagnosticReport report)
        {
            AddResult("  ── Browser Object Methods ──");
            var type = browser.GetType();
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.IsSpecialName) continue;
                var parms = string.Join(", ",
                    m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                AddResult($"    {m.ReturnType.Name} {m.Name}({parms})");
            }

            // Try to browse root level
            try
            {
                // Try Browse() with no args
                var browseMethod = type.GetMethod("Browse", new Type[0]);
                if (browseMethod != null)
                {
                    AddResult("  Calling browser.Browse()...");
                    var elements = browseMethod.Invoke(browser, null);
                    if (elements != null && elements is Array arr)
                    {
                        AddResult($"  Got {arr.Length} elements at root:");
                        int count = 0;
                        foreach (var elem in arr)
                        {
                            if (count >= 20) { AddResult("  ... (truncated)"); break; }
                            AddResult($"    [{count}] {elem}");
                            // Get properties
                            foreach (var p in elem.GetType().GetProperties())
                            {
                                try
                                {
                                    AddResult($"        .{p.Name} = {p.GetValue(elem)}");
                                }
                                catch { }
                            }
                            count++;
                        }
                        report.BrowseRootTags = arr.Length;
                    }
                }

                // Try ShowBranches/ShowLeafs pattern
                var showBranches = type.GetMethod("ShowBranches");
                var showLeafs = type.GetMethod("ShowLeafs");
                if (showBranches != null)
                {
                    AddResult("  Calling ShowBranches()...");
                    showBranches.Invoke(browser, null);
                    var browseAfter = type.GetMethod("Browse", new Type[0]);
                    if (browseAfter != null)
                    {
                        var items = browseAfter.Invoke(browser, null);
                        if (items is Array branchArr)
                            AddResult($"  Branches: {branchArr.Length}");
                    }
                }
            }
            catch (Exception ex)
            {
                AddResult($"  Browse FAILED: {ex.InnerException?.Message ?? ex.Message}");
            }

            // Try to dispose/cleanup
            if (browser is IDisposable disp)
                disp.Dispose();
        }

        private void TryBrowseDirect(DiagnosticReport report)
        {
            AddResult("═══ SDK Browse() Direct ═══");
            try
            {
                var server = _connection.Server;
                // Some SDK versions have Browse directly on the server
                var browseMethod = server.GetType().GetMethod("Browse",
                    BindingFlags.Public | BindingFlags.Instance);

                if (browseMethod != null)
                {
                    var parms = browseMethod.GetParameters();
                    AddResult($"  Found Browse on server: {parms.Length} params");

                    if (parms.Length == 0)
                    {
                        var result = browseMethod.Invoke(server, null);
                        AddResult($"  Result: {result}");
                    }
                }
                else
                {
                    AddResult("  No Browse method directly on TsCHdaServer");
                }
            }
            catch (Exception ex)
            {
                AddResult($"  Browse direct FAILED: {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        private void TryReadWithDifferentPaths(DiagnosticReport report)
        {
            AddResult("═══ TAG PATH FORMAT TESTS ═══");

            // Try various common KepServerEX tag path formats
            string[] testPaths = new[]
            {
                // Full historian path formats
                "_LocalHistorian.Datastore._ImportedTags.PLF_A10.A10.10QT0002",
                "Local Historian._ImportedTags.PLF_A10.A10.10QT0002",
                "_LocalHistorian._ImportedTags.PLF_A10.A10.10QT0002",

                // Short paths (post-GetItemID)
                "PLF_A10.A10.10QT0002",
                "PLF_A10.A10.10QT0002.",

                // Channel.Device.Tag format (standard KepServerEX)
                "Channel1.Device1.Tag1",
            };

            var server = _connection.Server;
            var now = DateTime.UtcNow;
            var start = now.AddHours(-24);

            foreach (var path in testPaths)
            {
                try
                {
                    var trend = new TsCHdaTrend(server)
                    {
                        StartTime     = new TsCHdaTime(start),
                        EndTime       = new TsCHdaTime(now),
                        IncludeBounds = true,
                        MaxValues     = 5,
                    };

                    var item = trend.AddItem(new OpcItem(path));
                    AddResult($"  AddItem('{path}') → ClientHandle={item.ClientHandle}, ServerHandle={item.ServerHandle}");

                    var collections = trend.ReadRaw(new[] { item });
                    if (collections != null && collections.Length > 0)
                    {
                        var col = collections[0];
                        AddResult($"    ReadRaw → {col.Count} points, Result={col.Result}");
                        if (col.Count > 0)
                        {
                            report.WorkingTagPaths.Add(path);
                            AddResult($"    *** SUCCESS! Tag path '{path}' WORKS! ***");
                            foreach (TsCHdaItemValue val in col)
                            {
                                AddResult($"      {val.Timestamp:O} = {val.Value} ({val.Quality})");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    string msg = ex.InnerException?.Message ?? ex.Message;
                    // Truncate long messages
                    if (msg.Length > 120) msg = msg.Substring(0, 120) + "...";
                    AddResult($"  '{path}' → ERROR: {msg}");
                }
            }
        }

        private void TryRawComQI(DiagnosticReport report)
        {
            AddResult("═══ RAW COM QUERYINTERFACE ANALYSIS ═══");

            try
            {
                var rawCom = _connection.GetRawComObject();
                AddResult($"  Raw COM object type: {rawCom.GetType().FullName}");

                IntPtr pUnk = Marshal.GetIUnknownForObject(rawCom);
                AddResult($"  IUnknown ptr: 0x{pUnk.ToInt64():X}");

                // Try QI for various OPC interfaces
                var interfacesToTest = new Dictionary<string, string>
                {
                    { "IOPCHDA_Server",      "1F1217B1-DEE0-11d2-A5E5-000086339399" },
                    { "IOPCHDA_Browser",     "1F1217B3-DEE0-11d2-A5E5-000086339399" },
                    { "IOPCHDA_SyncRead",    "1F1217B5-DEE0-11d2-A5E5-000086339399" },
                    { "IOPCHDA_AsyncRead",   "1F1217B7-DEE0-11d2-A5E5-000086339399" },
                    { "IOPCCommon",          "F31DFDE2-07B6-11d2-B2D8-0060083BA1FB" },
                    { "IOPCServer",          "39c13a4d-011e-11d0-9675-0020afd8adb3" },
                    { "IOPCItemProperties",  "39c13a72-011e-11d0-9675-0020afd8adb3" },
                    { "IOPCBrowseServerAddressSpace", "39c13a4f-011e-11d0-9675-0020afd8adb3" },
                    { "IUnknown",            "00000000-0000-0000-C000-000000000046" },
                    { "IDispatch",           "00020400-0000-0000-C000-000000000046" },
                };

                report.QIResults = new Dictionary<string, string>();

                foreach (var kvp in interfacesToTest)
                {
                    Guid iid = new Guid(kvp.Value);
                    int hr = Marshal.QueryInterface(pUnk, ref iid, out IntPtr pInterface);

                    string status;
                    if (hr == 0 && pInterface != IntPtr.Zero)
                    {
                        status = "✓ SUPPORTED";
                        Marshal.Release(pInterface);
                    }
                    else
                    {
                        status = $"✗ hr=0x{hr:X8} ({HresultToString(hr)})";
                    }

                    report.QIResults[kvp.Key] = status;
                    AddResult($"  {kvp.Key,-35} {status}");
                }

                Marshal.Release(pUnk);

                // Also try QI on the _server object directly (not the unknown_ inside it)
                AddResult("");
                AddResult("  ── Trying QI on TsCHdaServer._server directly ──");
                object innerServer = ComInterop.ReflectionHelper.GetField(_connection.Server, "_server");
                if (innerServer != null && innerServer.GetType().Name != "__ComObject")
                {
                    // The inner server is a managed wrapper — check if IT has a COM object
                    AddResult($"  _server type: {innerServer.GetType().FullName}");

                    // Try to find any __ComObject in its fields
                    Type ist = innerServer.GetType();
                    while (ist != null && ist != typeof(object))
                    {
                        foreach (var fi in ist.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
                        {
                            try
                            {
                                var val = fi.GetValue(innerServer);
                                if (val != null && val.GetType().Name == "__ComObject")
                                {
                                    AddResult($"  Found __ComObject at {ist.Name}.{fi.Name}");
                                    IntPtr pUnk2 = Marshal.GetIUnknownForObject(val);

                                    foreach (var kvp in interfacesToTest)
                                    {
                                        Guid iid = new Guid(kvp.Value);
                                        int hr = Marshal.QueryInterface(pUnk2, ref iid, out IntPtr pIface);
                                        if (hr == 0 && pIface != IntPtr.Zero)
                                        {
                                            AddResult($"    {kvp.Key,-35} ✓ SUPPORTED on {fi.Name}");
                                            Marshal.Release(pIface);
                                        }
                                    }
                                    Marshal.Release(pUnk2);
                                }
                            }
                            catch { }
                        }
                        ist = ist.BaseType;
                    }
                }
            }
            catch (Exception ex)
            {
                AddResult($"  COM QI analysis FAILED: {ex.Message}");
            }
        }

        private void CheckThreading(DiagnosticReport report)
        {
            AddResult("═══ THREADING INFO ═══");
            var thread = System.Threading.Thread.CurrentThread;
            AddResult($"  Thread ID: {thread.ManagedThreadId}");
            AddResult($"  Apartment: {thread.GetApartmentState()}");
            AddResult($"  IsBackground: {thread.IsBackground}");
            AddResult($"  IsThreadPoolThread: {thread.IsThreadPoolThread}");
        }

        private string HresultToString(int hr)
        {
            switch (unchecked((uint)hr))
            {
                case 0x80004002: return "E_NOINTERFACE";
                case 0x80004001: return "E_NOTIMPL";
                case 0x80004005: return "E_FAIL";
                case 0x80070005: return "E_ACCESSDENIED";
                case 0x800401FD: return "CO_E_OBJNOTCONNECTED";
                case 0x80010108: return "RPC_E_DISCONNECTED";
                case 0x8001010E: return "RPC_E_WRONG_THREAD";
                default: return "unknown";
            }
        }

        private void AddResult(string line)
        {
            _results.Add(line);
            Log.Information("[DIAG] {Line}", line);
        }
    }

    public class DiagnosticReport
    {
        public List<string> Steps             { get; set; } = new List<string>();
        public List<string> SdkMethods        { get; set; } = new List<string>();
        public List<string> SdkProperties     { get; set; } = new List<string>();
        public string       ServerStatus      { get; set; }
        public bool         GetStatusWorked   { get; set; }
        public bool         CreateBrowserWorked { get; set; }
        public int          BrowseRootTags    { get; set; }
        public List<string> WorkingTagPaths   { get; set; } = new List<string>();
        public Dictionary<string, string> QIResults { get; set; }
        public string       FatalError        { get; set; }
    }
}
