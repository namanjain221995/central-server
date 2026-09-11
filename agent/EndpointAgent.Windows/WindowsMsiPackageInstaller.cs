using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using EndpointAgent.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Windows;

/// <summary>
/// Installs MSI packages through the Windows Installer service (<c>msi.dll</c>)
/// and verifies their Authenticode signature through WinVerifyTrust
/// (<c>wintrust.dll</c>).
/// </summary>
/// <remarks>
/// <para>
/// No process is launched and no shell is invoked (ADR-0005): installation is
/// driven by <c>MsiInstallProduct</c>, product detection by
/// <c>MsiQueryProductState</c>, both direct API calls. This is what lets the
/// agent gain a real install capability without reintroducing the arbitrary
/// -execution surface the whole design is built to avoid.
/// </para>
/// <para>
/// Two independent gates protect the install: the caller has already verified the
/// content hash, and this type verifies (a) the file carries a trusted
/// Authenticode signature via WinVerifyTrust and (b) the signer subject contains
/// the pinned string. Only then is a byte handed to the installer. Reboots are
/// suppressed; an install that wants one is reported, never performed silently.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsMsiPackageInstaller(ILogger<WindowsMsiPackageInstaller> logger) : IPackageInstaller
{
    private const int InstallStateDefault = 5;       // INSTALLSTATE_DEFAULT: installed and usable.
    private const uint ErrorSuccess = 0;
    private const uint ErrorSuccessRebootRequired = 3010;
    private const uint InstallUiLevelNone = 2;       // INSTALLUILEVEL_NONE

    private readonly ILogger<WindowsMsiPackageInstaller> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public ValueTask<bool> IsProductInstalledAsync(
        string msiProductCode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(msiProductCode))
        {
            return ValueTask.FromResult(false);
        }

        try
        {
            var state = NativeMethods.MsiQueryProductState(msiProductCode);
            return ValueTask.FromResult(state == InstallStateDefault);
        }
        catch (DllNotFoundException ex)
        {
            // msi.dll genuinely absent (not expected on Windows) - report not installed
            // rather than crash. An EntryPointNotFoundException, by contrast, is a
            // binding bug and is deliberately NOT swallowed: reporting "not installed"
            // for a broken P/Invoke would trigger spurious re-installs.
            _logger.LogWarning(ex, "Windows Installer is unavailable on this host.");
            return ValueTask.FromResult(false);
        }
    }

    public ValueTask<PackageInstallOutcome> InstallAsync(
        string msiPath, string? requiredSignerSubject, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(msiPath))
        {
            return ValueTask.FromResult(new PackageInstallOutcome(
                PackageInstallResult.InstallFailed, null, "Package file was not found on disk."));
        }

        // Gate 1: Authenticode trust + signer subject pin. An unsigned or untrusted
        // file, or a signer that does not match, is refused before any install.
        var signatureError = VerifySignature(msiPath, requiredSignerSubject);
        if (signatureError is not null)
        {
            return ValueTask.FromResult(new PackageInstallOutcome(
                PackageInstallResult.SignatureRejected, null, signatureError));
        }

        // Quiet, no UI. The service runs as LocalSystem; MSI requires elevation.
        NativeMethods.MsiSetInternalUI(InstallUiLevelNone, IntPtr.Zero);

        uint code;
        try
        {
            code = NativeMethods.MsiInstallProduct(msiPath, "REBOOT=ReallySuppress");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return ValueTask.FromResult(new PackageInstallOutcome(
                PackageInstallResult.InstallFailed, null, "Windows Installer is unavailable on this host."));
        }

        return ValueTask.FromResult(code switch
        {
            ErrorSuccess => new PackageInstallOutcome(PackageInstallResult.Succeeded, code, "Installed."),
            ErrorSuccessRebootRequired => new PackageInstallOutcome(
                PackageInstallResult.SucceededRebootRequired, code, "Installed; reboot required."),
            _ => new PackageInstallOutcome(
                PackageInstallResult.InstallFailed, code, $"Windows Installer returned {code}."),
        });
    }

    /// <summary>Returns null when the signature is acceptable, otherwise a reason.</summary>
    private string? VerifySignature(string filePath, string? requiredSignerSubject)
    {
        // WinVerifyTrust: is the file signed by a trusted publisher and untampered?
        var trustResult = WinTrust.VerifyEmbeddedSignature(filePath);
        if (trustResult != 0)
        {
            return $"Authenticode verification failed (0x{trustResult:X8}).";
        }

        if (string.IsNullOrWhiteSpace(requiredSignerSubject))
        {
            return null; // Trusted signature is sufficient; no specific signer pinned.
        }

        try
        {
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
            if (cert.Subject.Contains(requiredSignerSubject, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            _logger.LogWarning(
                "Package signer subject did not match the pin. Required to contain '{Required}'.",
                requiredSignerSubject);
            return "Signer subject does not match the required publisher.";
        }
        catch (Exception ex) when (ex is CryptographicException or IOException)
        {
            return "Could not read the package signer certificate.";
        }
    }

    // The msi.dll bindings the installer needs, and only those. It was briefly a
    // shared set with an application remover; that feature was withdrawn, and the
    // bindings it alone used (MsiConfigureProductEx, MsiGetProductInfo,
    // MsiEnumRelatedProducts) went with it rather than being left declared for a
    // caller that no longer exists.
    private static class NativeMethods
    {
        // msi.dll exports the Unicode string entry points with a W suffix; name them
        // explicitly rather than relying on marshaller name-mangling.
        [DllImport("msi.dll", EntryPoint = "MsiQueryProductStateW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int MsiQueryProductState(string szProduct);

        [DllImport("msi.dll", EntryPoint = "MsiInstallProductW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint MsiInstallProduct(string szPackagePath, string szCommandLine);

        // No string parameters, so there is no A/W variant.
        [DllImport("msi.dll", ExactSpelling = true)]
        internal static extern uint MsiSetInternalUI(uint dwUILevel, IntPtr phWnd);

    }
}
