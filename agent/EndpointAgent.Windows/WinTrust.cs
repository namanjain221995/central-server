using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EndpointAgent.Windows;

/// <summary>
/// Thin wrapper over <c>WinVerifyTrust</c> for validating a file's embedded
/// Authenticode signature. Returns 0 when the file is signed by a trusted
/// publisher and has not been tampered with; a non-zero HRESULT otherwise (e.g.
/// <c>TRUST_E_NOSIGNATURE</c>, <c>TRUST_E_BAD_DIGEST</c>,
/// <c>CERT_E_UNTRUSTEDROOT</c>).
/// </summary>
/// <remarks>
/// Revocation is deliberately not checked, so verification carries no revocation
/// network dependency. Chain building, by contrast, is allowed to retrieve a
/// missing intermediate or root certificate over the network: a package from a
/// publisher whose CA certificates are not already cached on the endpoint -- a
/// freshly provisioned machine that has never verified, say, a Sectigo-signed
/// file -- otherwise fails with <c>CERT_E_CHAINING</c> (0x800B010A) even though
/// its signature is perfectly valid. The install path is online by definition
/// (the package was just downloaded), so permitting that retrieval is what lets a
/// new device verify the same packages a developer's machine already can. This is
/// a trust decision about the publisher chain, not a freshness guarantee.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class WinTrust
{
    // WINTRUST_ACTION_GENERIC_VERIFY_V2
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdRevocationCheckNone = 0x00000010;

    public static int VerifyEmbeddedSignature(string filePath)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = filePath,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero,
        };

        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        var pData = IntPtr.Zero;

        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, fDeleteOld: false);

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                pPolicyCallbackData = IntPtr.Zero,
                pSIPClientData = IntPtr.Zero,
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = WtdRevokeNone,
                dwUnionChoice = WtdChoiceFile,
                pFile = pFile,
                dwStateAction = WtdStateActionVerify,
                hWVTStateData = IntPtr.Zero,
                pwszURLReference = IntPtr.Zero,
                dwProvFlags = WtdRevocationCheckNone,
                dwUIContext = 0,
                pSignatureSettings = IntPtr.Zero,
            };

            pData = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
            Marshal.StructureToPtr(data, pData, fDeleteOld: false);

            var action = GenericVerifyV2;
            var result = NativeMethods.WinVerifyTrust(IntPtr.Zero, ref action, pData);

            // Always release the state handle the VERIFY action allocated.
            data.dwStateAction = WtdStateActionClose;
            Marshal.StructureToPtr(data, pData, fDeleteOld: true);
            NativeMethods.WinVerifyTrust(IntPtr.Zero, ref action, pData);

            return result;
        }
        finally
        {
            if (pData != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(pData);
            }

            Marshal.FreeHGlobal(pFile);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    private static class NativeMethods
    {
        [DllImport("wintrust.dll", ExactSpelling = true)]
        internal static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, IntPtr pWVTData);
    }
}
