// ═══════════════════════════════════════════════════════════════════════════
// REFLECTION HELPERS
// ───────────────────────────────────────────────────────────────────────────
// Ported from hdatomqtt/ConsoleApplication.cs
//
// TsCHdaServer does not expose its internal COM connection publicly.
// These helpers reach the raw COM object via:
//   TsCHdaServer._server → OpcClientSdk.Com.Hda.Server
//     └── unknown_ (base class) → System.__ComObject
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Reflection;

namespace OpcHdaBroker.ComInterop
{
    /// <summary>
    /// Reflection utilities for accessing private fields in the Technosoftware SDK.
    /// Required because the SDK does not expose the raw COM connection object.
    /// </summary>
    public static class ReflectionHelper
    {
        /// <summary>
        /// Gets a private/public instance field from the object's DECLARED type only.
        /// </summary>
        public static object GetField(object obj, string name)
            => obj?.GetType()
                   .GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                   ?.GetValue(obj);

        /// <summary>
        /// Gets a private/public instance field by walking the ENTIRE base class chain.
        /// Needed because "unknown_" is declared on a parent class (OpcClientSdk.Com.Server),
        /// not on the concrete type (OpcClientSdk.Com.Hda.Server).
        /// </summary>
        public static object GetFieldFromChain(object obj, string name)
        {
            if (obj == null) return null;
            Type t = obj.GetType();
            while (t != null && t != typeof(object))
            {
                var fi = t.GetField(name,
                    BindingFlags.NonPublic | BindingFlags.Public |
                    BindingFlags.Instance  | BindingFlags.DeclaredOnly);
                if (fi != null) return fi.GetValue(obj);
                t = t.BaseType;
            }
            return null;
        }
    }
}
