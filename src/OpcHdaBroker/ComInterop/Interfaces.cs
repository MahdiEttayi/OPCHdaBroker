// ═══════════════════════════════════════════════════════════════════════════
// COM INTERFACE DEFINITIONS — OPC HDA 1.20 Specification
// ───────────────────────────────────────────────────────────────────────────
// Ported from hdatomqtt/ConsoleApplication.cs
//
// These interfaces are declared inline to avoid DLL conflicts with
// OpcClientSdk472. GUIDs are from the official OPC HDA 1.20 IDL spec.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Runtime.InteropServices;

namespace OpcHdaBroker.ComInterop
{
    /// <summary>
    /// IOPCHDA_Server — main OPC HDA server interface.
    /// Used to obtain the namespace browser via CreateBrowse().
    /// </summary>
    [ComImport]
    [Guid("1F1217B1-DEE0-11d2-A5E5-000086339399")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IOPCHDA_Server
    {
        void GetAggregates(
            [Out] out int pdwCount,
            [Out] out IntPtr ppdwAggrID,
            [Out] out IntPtr ppszAggrName,
            [Out] out IntPtr ppszAggrDesc);

        void GetHistorianStatus(
            [Out, MarshalAs(UnmanagedType.LPWStr)] out string pwszStatus,
            [Out] out System.Runtime.InteropServices.ComTypes.FILETIME pftCurrentTime,
            [Out] out System.Runtime.InteropServices.ComTypes.FILETIME pftStartTime,
            [Out] out short pwMajorVersion,
            [Out] out short pwMinorVersion,
            [Out] out short pwBuildNumber,
            [Out, MarshalAs(UnmanagedType.LPWStr)] out string pwszVendorInfo);

        void GetItemAttributes(
            [Out] out int pdwCount,
            [Out] out IntPtr ppAttrID,
            [Out] out IntPtr ppszAttrName,
            [Out] out IntPtr ppszAttrDesc,
            [Out] out IntPtr ppvtAttrDataType);

        void GetItemHandles(
            [In]  int dwCount,
            [In,  MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] string[] pszItemIDs,
            [In,  MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] int[]    phClientItems,
            [Out] out IntPtr phServerItems,
            [Out] out IntPtr ppErrors);

        void ReleaseItemHandles(
            [In]  int dwCount,
            [In,  MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] int[] phServerItems,
            [Out] out IntPtr ppErrors);

        void ValidateItemIDs(
            [In]  int dwCount,
            [In,  MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] string[] pszItemIDs,
            [Out] out IntPtr ppErrors);

        void CreateBrowse(
            [In]  int dwCount,
            [In,  MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] int[]    pdwAttrIDs,
            [In,  MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] int[]    pOperator,
            [In,  MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] object[] vFilter,
            [Out, MarshalAs(UnmanagedType.IUnknown)] out object ppIHDABrowser,
            [Out] out IntPtr ppErrors);
    }

    /// <summary>
    /// IOPCHDA_Browser — namespace walker returned by CreateBrowse().
    /// The OPC HDA namespace is a tree (like a file system):
    ///   _LocalHistorian
    ///     └── Datastore
    ///           └── _ImportedTags
    ///                 └── PLF_A10
    ///                       └── A10
    ///                             └── 10QT0002  ← leaf = historized tag
    /// </summary>
    [ComImport]
    [Guid("1F1217B3-DEE0-11d2-A5E5-000086339399")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IOPCHDA_Browser
    {
        void GetEnum(
            [In]  uint dwBrowseType,
            [Out, MarshalAs(UnmanagedType.IUnknown)] out object ppIEnumString);

        void ChangeBrowsePosition(
            [In]  uint dwBrowseDirection,
            [In,  MarshalAs(UnmanagedType.LPWStr)] string szString);

        void GetItemID(
            [In,  MarshalAs(UnmanagedType.LPWStr)] string szLeaf,
            [Out, MarshalAs(UnmanagedType.LPWStr)] out string pszItemID);

        void GetBranchPosition(
            [Out, MarshalAs(UnmanagedType.LPWStr)] out string pszBranchPos);
    }

    /// <summary>
    /// IEnumString — standard COM string enumerator.
    /// Renamed to IEnumStringLocal to avoid conflicts.
    /// </summary>
    [ComImport]
    [Guid("00000101-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IEnumStringLocal
    {
        [PreserveSig]
        int Next(
            [In]  int celt,
            [Out, MarshalAs(UnmanagedType.LPArray,
                ArraySubType   = UnmanagedType.LPWStr,
                SizeParamIndex = 0)] string[] rgelt,
            [Out] out int pceltFetched);

        [PreserveSig] int Skip([In] int celt);
        [PreserveSig] int Reset();
        void Clone([Out] out IEnumStringLocal ppenum);
    }

    /// <summary>
    /// OPC HDA browse direction and type constants from opchda.idl.
    /// </summary>
    public static class HdaBrowseConstants
    {
        public const uint BROWSE_UP     = 1;
        public const uint BROWSE_DOWN   = 2;
        public const uint BROWSE_DIRECT = 3;

        public const uint BRANCH = 1;  // enumerate folder children
        public const uint LEAF   = 2;  // enumerate tag children
        public const uint FLAT   = 3;  // enumerate everything
    }
}
