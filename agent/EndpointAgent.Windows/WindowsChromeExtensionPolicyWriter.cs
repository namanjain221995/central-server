using System.Runtime.Versioning;
using System.Security;
using EndpointAgent.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace EndpointAgent.Windows;

/// <summary>
/// Writes the Chrome <c>ExtensionSettings</c> enterprise policy machine-wide:
/// <c>HKLM\SOFTWARE\Policies\Google\Chrome</c>, value <c>ExtensionSettings</c>,
/// <c>REG_SZ</c> holding the JSON document -- how Chrome carries a dictionary
/// policy in the Windows registry. Chrome reads it and does the enforcing; the
/// agent never touches Chrome itself.
/// </summary>
/// <remarks>
/// <para>
/// Pure <c>Microsoft.Win32</c> registry data written from the LocalSystem
/// service, exactly as ADR-0005 requires: no process launch, no Chrome binary
/// invoked, nothing composed and run. The 64-bit registry view is used
/// explicitly, because that is the view Chrome's policy reader consults on the
/// 64-bit Windows this agent supports (the RustDesk reader's convention).
/// </para>
/// <para>
/// <b>The writer owns one value, not the key.</b> <see cref="Clear"/> deletes the
/// <c>ExtensionSettings</c> value and nothing else -- the <c>Google\Chrome</c>
/// policy key is shared ground where an administrator's other Chrome policies may
/// live, so the key itself is never deleted, even when this writer created it.
/// </para>
/// <para>
/// An expected Windows failure (access denied, a registry error) is logged and
/// returned as false, never thrown, so the executor fails the task honestly: the
/// server is told the policy is not in place rather than shown a green tick next
/// to a control that is not there.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsChromeExtensionPolicyWriter(
    ILogger<WindowsChromeExtensionPolicyWriter> logger) : IChromeExtensionPolicyWriter
{
    /// <summary>Chrome's machine policy key, relative to HKLM. Shared with other Chrome policies.</summary>
    internal const string ChromePolicyKeyPath = @"SOFTWARE\Policies\Google\Chrome";

    /// <summary>The one value this writer owns under <see cref="ChromePolicyKeyPath"/>.</summary>
    internal const string ExtensionSettingsValueName = "ExtensionSettings";

    private readonly ILogger<WindowsChromeExtensionPolicyWriter> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public bool Apply(string settingsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsJson);

        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.CreateSubKey(ChromePolicyKeyPath, writable: true);
            key.SetValue(ExtensionSettingsValueName, settingsJson, RegistryValueKind.String);

            _logger.LogInformation(
                @"Chrome extension policy written to HKLM\{Key}\{Value} ({Length} characters).",
                ChromePolicyKeyPath, ExtensionSettingsValueName, settingsJson.Length);
            return true;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.LogError(
                ex, @"Could not write the Chrome extension policy value under HKLM\{Key}.", ChromePolicyKeyPath);
            return false;
        }
    }

    /// <inheritdoc />
    public bool Clear()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(ChromePolicyKeyPath, writable: true);

            // No key means no policy: other Chrome policies were never written on
            // this machine, so there is nothing of ours to remove.
            if (key is null)
            {
                _logger.LogInformation("Chrome extension policy already absent; nothing to clear.");
                return true;
            }

            // Only our value. The key stays, whatever else lives in it.
            key.DeleteValue(ExtensionSettingsValueName, throwOnMissingValue: false);

            _logger.LogInformation(
                @"Chrome extension policy cleared from HKLM\{Key}\{Value}.",
                ChromePolicyKeyPath, ExtensionSettingsValueName);
            return true;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.LogError(
                ex, @"Could not clear the Chrome extension policy value under HKLM\{Key}.", ChromePolicyKeyPath);
            return false;
        }
    }
}
