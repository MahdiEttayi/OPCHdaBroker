// ═══════════════════════════════════════════════════════════════════════════
// HDA CONNECTION MANAGER
// ───────────────────────────────────────────────────────────────────────────
// Manages the lifecycle of the OPC HDA COM connection to KepServerEX.
// Handles connect, reconnect, and health status queries.
// All public methods are designed to be called from the STA dispatcher.
//
// IMPORTANT: The TsCHdaServer.Connect() must happen on the SAME thread
// that will later do QueryInterface / COM calls. The STA thread dispatcher
// ensures this — all COM work runs on a single dedicated thread.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using OpcClientSdk;
using OpcClientSdk.Hda;
using Serilog;

namespace OpcHdaBroker.ComInterop
{
    /// <summary>
    /// Owns the single connection to KepServerEX OPC HDA.
    /// Thread-unsafe by design — all calls must go through <see cref="StaThreadDispatcher"/>.
    /// </summary>
    public sealed class HdaConnection : IDisposable
    {
        private static readonly ILogger Log = Serilog.Log.ForContext<HdaConnection>();

        private readonly string _primaryUrl;
        private readonly string _fallbackUrl;

        private TsCHdaServer _server;
        private bool _connected;

        public bool IsConnected => _connected;
        public TsCHdaServer Server => _server;

        public HdaConnection(string primaryUrl, string fallbackUrl)
        {
            _primaryUrl  = primaryUrl  ?? throw new ArgumentNullException(nameof(primaryUrl));
            _fallbackUrl = fallbackUrl ?? primaryUrl;
        }

        /// <summary>
        /// Connect to KepServerEX. Tries primary URL first, then fallback.
        /// Must be called from the STA thread.
        /// </summary>
        public void Connect()
        {
            if (_connected) return;

            _server = new TsCHdaServer();
            string[] urls = { _primaryUrl, _fallbackUrl };
            Exception lastEx = null;

            foreach (string url in urls)
            {
                try
                {
                    Log.Information("Connecting to OPC HDA: {Url}", url);
                    _server.Connect(url);
                    _connected = true;
                    Log.Information("Connected to OPC HDA server successfully");

                    // Diagnostic: dump reflection path immediately after connect
                    DumpReflectionDiagnostics();
                    return;
                }
                catch (Exception ex)
                {
                    Log.Warning("Connection to {Url} failed: {Message}", url, ex.Message);
                    lastEx = ex;
                }
            }

            throw new InvalidOperationException(
                $"Cannot connect to OPC HDA server. Last error: {lastEx?.Message}", lastEx);
        }

        /// <summary>
        /// Disconnect and dispose the server connection.
        /// </summary>
        public void Disconnect()
        {
            if (_server != null && _connected)
            {
                try
                {
                    _server.Disconnect();
                    Log.Information("Disconnected from OPC HDA server");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Error during disconnect");
                }
            }
            _connected = false;
        }

        /// <summary>
        /// Reconnect by disconnecting and connecting again.
        /// </summary>
        public void Reconnect()
        {
            Log.Information("Reconnecting to OPC HDA server...");
            Disconnect();
            DisposeServer();
            Connect();
        }

        /// <summary>
        /// Get the raw COM object from the SDK wrapper for direct COM interop.
        /// Tries multiple reflection paths to handle different SDK internal structures.
        ///
        /// Known paths:
        ///   Path A: TsCHdaServer._server → .unknown_           (OpcClientSdk472 typical)
        ///   Path B: TsCHdaServer._server → .m_server           (some SDK versions)
        ///   Path C: TsCHdaServer.m_server → .unknown_           (alternate field names)
        /// </summary>
        public object GetRawComObject()
        {
            EnsureConnected();

            // Path A: _server → unknown_  (the proven path from hdatomqtt)
            object innerServer = ReflectionHelper.GetField(_server, "_server");
            if (innerServer != null)
            {
                Log.Debug("Reflection: _server found → type={Type}", innerServer.GetType().FullName);

                object rawCom = ReflectionHelper.GetFieldFromChain(innerServer, "unknown_");
                if (rawCom != null)
                {
                    Log.Debug("Reflection: unknown_ found → type={Type}", rawCom.GetType().FullName);
                    return rawCom;
                }

                // Try m_server on the inner object
                rawCom = ReflectionHelper.GetFieldFromChain(innerServer, "m_server");
                if (rawCom != null)
                {
                    Log.Debug("Reflection: m_server found → type={Type}", rawCom.GetType().FullName);
                    return rawCom;
                }

                // The inner server itself might be the COM object if it's a __ComObject
                if (innerServer.GetType().Name == "__ComObject")
                {
                    Log.Debug("Reflection: _server IS the __ComObject directly");
                    return innerServer;
                }

                // Last resort: try QI directly on the inner server
                Log.Debug("Reflection: trying QI directly on _server ({Type})", innerServer.GetType().FullName);
                return innerServer;
            }

            // Path C: m_server directly on TsCHdaServer
            innerServer = ReflectionHelper.GetField(_server, "m_server");
            if (innerServer != null)
            {
                Log.Debug("Reflection: m_server found directly → type={Type}", innerServer.GetType().FullName);
                object rawCom = ReflectionHelper.GetFieldFromChain(innerServer, "unknown_");
                return rawCom ?? innerServer;
            }

            throw new InvalidOperationException(
                "Cannot find raw COM object — SDK internal structure may have changed. " +
                "Run DumpReflectionDiagnostics() to inspect the object hierarchy.");
        }

        /// <summary>
        /// Obtain IOPCHDA_Server interface from the raw COM object via QueryInterface.
        /// Tries multiple COM objects in the reflection chain until QI succeeds.
        /// </summary>
        internal IOPCHDA_Server GetIOPCHDA_Server()
        {
            EnsureConnected();

            // Strategy: walk the reflection chain and try QI on each object
            // until one succeeds. Different SDK versions may store the COM
            // object at different depths.
            object innerServer = ReflectionHelper.GetField(_server, "_server");
            if (innerServer == null)
                throw new InvalidOperationException("_server field is null");

            // Collect candidate COM objects to try QI on
            var candidates = new System.Collections.Generic.List<(string name, object obj)>();

            // Candidate 1: unknown_ (the typical path)
            object unknown = ReflectionHelper.GetFieldFromChain(innerServer, "unknown_");
            if (unknown != null)
                candidates.Add(("unknown_", unknown));

            // Candidate 2: m_server
            object mServer = ReflectionHelper.GetFieldFromChain(innerServer, "m_server");
            if (mServer != null)
                candidates.Add(("m_server", mServer));

            // Candidate 3: the inner server object itself
            candidates.Add(("_server", innerServer));

            // Try QI on each candidate
            Guid iid = typeof(IOPCHDA_Server).GUID;
            foreach (var (name, candidate) in candidates)
            {
                try
                {
                    IntPtr pUnk = Marshal.GetIUnknownForObject(candidate);
                    int hr = Marshal.QueryInterface(pUnk, ref iid, out IntPtr pHda);
                    Marshal.Release(pUnk);

                    if (hr == 0 && pHda != IntPtr.Zero)
                    {
                        Log.Information("QI for IOPCHDA_Server succeeded on '{Name}' (type={Type})",
                            name, candidate.GetType().FullName);
                        var hdaSrv = (IOPCHDA_Server)Marshal.GetTypedObjectForIUnknown(pHda, typeof(IOPCHDA_Server));
                        Marshal.Release(pHda);
                        return hdaSrv;
                    }
                    else
                    {
                        Log.Debug("QI for IOPCHDA_Server failed on '{Name}' (hr=0x{Hr:X8})", name, hr);
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug("QI attempt on '{Name}' threw: {Message}", name, ex.Message);
                }
            }

            // All candidates failed — dump full diagnostic and throw
            DumpReflectionDiagnostics();
            throw new COMException(
                "QueryInterface for IOPCHDA_Server failed on all candidates. " +
                "Check logs for reflection diagnostics.");
        }

        /// <summary>
        /// Query server status using the SDK's built-in GetServerStatus().
        /// No raw COM QI needed.
        /// </summary>
        public HistorianStatus GetStatus()
        {
            EnsureConnected();
            try
            {
                OpcServerStatus serverStatus = _server.GetServerStatus();

                return new HistorianStatus
                {
                    Status     = serverStatus?.ServerState.ToString() ?? "Unknown",
                    VendorInfo = serverStatus?.VendorInfo ?? "KepServerEX",
                    StartTime  = serverStatus?.StartTime ?? DateTime.MinValue,
                    CurrentTime = serverStatus?.CurrentTime ?? DateTime.UtcNow,
                    ProductVersion = serverStatus?.ProductVersion ?? "N/A"
                };
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "GetServerStatus failed");
                return new HistorianStatus { Status = "Error: " + ex.Message };
            }
        }

        /// <summary>
        /// Dumps every field in the TsCHdaServer object hierarchy to help
        /// diagnose which field holds the raw COM object.
        /// </summary>
        public void DumpReflectionDiagnostics()
        {
            Log.Information("═══ REFLECTION DIAGNOSTICS ═══");
            Log.Information("TsCHdaServer type: {Type}", _server?.GetType().FullName);

            // Dump all fields of TsCHdaServer
            DumpFields(_server, "TsCHdaServer", 0);

            // Dump _server field and its children
            object innerServer = ReflectionHelper.GetField(_server, "_server");
            if (innerServer != null)
            {
                Log.Information("_server → type={Type}", innerServer.GetType().FullName);
                DumpFields(innerServer, "_server", 1);

                // Dump unknown_ and its type
                object unknown = ReflectionHelper.GetFieldFromChain(innerServer, "unknown_");
                if (unknown != null)
                {
                    Log.Information("  unknown_ → type={Type}", unknown.GetType().FullName);

                    // Try to list COM interfaces on it
                    try
                    {
                        IntPtr pUnk = Marshal.GetIUnknownForObject(unknown);
                        Log.Information("  unknown_ IUnknown ptr = 0x{Ptr:X}", pUnk.ToInt64());
                        Marshal.Release(pUnk);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning("  Cannot get IUnknown from unknown_: {Msg}", ex.Message);
                    }
                }
                else
                {
                    Log.Warning("  unknown_ field NOT FOUND in inheritance chain");
                    // Walk and dump every field in the chain
                    Type t = innerServer.GetType();
                    while (t != null && t != typeof(object))
                    {
                        var fields = t.GetFields(
                            BindingFlags.NonPublic | BindingFlags.Public |
                            BindingFlags.Instance | BindingFlags.DeclaredOnly);
                        foreach (var fi in fields)
                        {
                            object val = null;
                            try { val = fi.GetValue(innerServer); } catch { }
                            Log.Information("  {DeclaringType}.{Name} ({FieldType}) = {Value}",
                                t.Name, fi.Name, fi.FieldType.Name,
                                val?.GetType().FullName ?? "null");
                        }
                        t = t.BaseType;
                    }
                }
            }
            else
            {
                Log.Warning("_server field NOT FOUND on TsCHdaServer");
            }

            Log.Information("═══ END DIAGNOSTICS ═══");
        }

        private void DumpFields(object obj, string label, int depth)
        {
            if (obj == null) return;
            string indent = new string(' ', depth * 2);
            Type t = obj.GetType();
            var fields = t.GetFields(
                BindingFlags.NonPublic | BindingFlags.Public |
                BindingFlags.Instance | BindingFlags.DeclaredOnly);

            foreach (var fi in fields)
            {
                string valType = "null";
                try
                {
                    object val = fi.GetValue(obj);
                    valType = val?.GetType().FullName ?? "null";
                }
                catch { valType = "<error>"; }

                Log.Debug("{Indent}{Label}.{Name} ({FieldType}) → {ValType}",
                    indent, label, fi.Name, fi.FieldType.Name, valType);
            }
        }

        private void EnsureConnected()
        {
            if (!_connected || _server == null)
                throw new InvalidOperationException("Not connected to OPC HDA server. Call Connect() first.");
        }

        private void DisposeServer()
        {
            try { _server?.Dispose(); } catch { /* ignore */ }
            _server = null;
        }

        public void Dispose()
        {
            Disconnect();
            DisposeServer();
        }
    }

    public class HistorianStatus
    {
        public string   Status         { get; set; }
        public string   VendorInfo     { get; set; }
        public string   ProductVersion { get; set; }
        public DateTime StartTime      { get; set; }
        public DateTime CurrentTime    { get; set; }
    }
}
