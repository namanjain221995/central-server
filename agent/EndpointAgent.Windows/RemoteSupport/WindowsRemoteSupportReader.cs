using System.Runtime.Versioning;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.RemoteSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace EndpointAgent.Windows.RemoteSupport;

/// <summary>
/// Reads the installed RustDesk's device ID from its own configuration file, for
/// reporting on the heartbeat.
/// </summary>
/// <remarks>
/// <para>
/// Read-only and process-free (ADR-0005): it opens one of a few well-known files and
/// one registry value, and nothing more. RustDesk keeps the ID in
/// <c>RustDesk.toml</c> -- as a plaintext <c>id</c> on older versions, and as an
/// encrypted <c>enc_id</c> on current ones (1.4.7+), sealed with this machine's UUID.
/// <see cref="RustDeskConfig"/> handles both shapes; this type contributes the file
/// locations, the MachineGuid the encrypted form needs, and the read.
/// </para>
/// <para>
/// Installed as a service -- the way Company Apps installs it -- RustDesk runs as
/// <c>LocalService</c> and keeps its config under that profile, so that is checked
/// first. <c>%ProgramData%</c> and the agent account's own roaming profile follow,
/// for other install shapes. An unreadable file or registry value is simply "no ID
/// this heartbeat": the ID is not a secret, and reading it must never disrupt the beat.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsRemoteSupportReader(ILogger<WindowsRemoteSupportReader> logger) : IRemoteSupportReader
{
    private readonly ILogger<WindowsRemoteSupportReader> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public string? ReadRustDeskId()
    {
        var machineGuid = ReadMachineGuid();

        foreach (var path in CandidateConfigPaths())
        {
            var text = TryReadAllText(path);
            if (text is null)
            {
                continue;
            }

            var id = RustDeskConfig.ParseDeviceId(text, machineGuid);
            if (!string.IsNullOrEmpty(id))
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>The RustDesk.toml locations, service install first, in read order.</summary>
    private static IEnumerable<string> CandidateConfigPaths()
    {
        // The installed service runs as LocalService, and RustDesk keeps RustDesk.toml
        // in that profile. Built from the Windows directory rather than hard-coded to C:.
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows))
        {
            yield return Path.Combine(
                windows, "ServiceProfiles", "LocalService", "AppData", "Roaming",
                "RustDesk", "config", "RustDesk.toml");
        }

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrEmpty(programData))
        {
            yield return Path.Combine(programData, "RustDesk", "config", "RustDesk.toml");
        }

        // The agent's own account (LocalSystem) roaming profile, for a RustDesk run as
        // SYSTEM rather than under the service account.
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            yield return Path.Combine(appData, "RustDesk", "config", "RustDesk.toml");
        }
    }

    /// <summary>
    /// The machine UUID RustDesk seals <c>enc_id</c> with: <c>HKLM\SOFTWARE\Microsoft\
    /// Cryptography\MachineGuid</c> from the 64-bit view, verbatim. Null if absent or
    /// unreadable, which simply leaves an encrypted ID unread.
    /// </summary>
    private string? ReadMachineGuid()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string;
        }
        catch (Exception ex)
            when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.LogDebug(
                ex, "Could not read MachineGuid; an encrypted RustDesk ID will not be read this heartbeat.");
            return null;
        }
    }

    private string? TryReadAllText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not read the RustDesk config at {Path}; reporting no ID.", path);
            return null;
        }
    }
}
