using System.Runtime.Versioning;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.RemoteSupport;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Windows.RemoteSupport;

/// <summary>
/// Reads the installed RustDesk's device ID from its own configuration file, for
/// reporting on the heartbeat.
/// </summary>
/// <remarks>
/// <para>
/// Read-only and process-free (ADR-0005): it opens one of two well-known files and
/// nothing more. RustDesk stores its ID in <c>RustDesk2.toml</c>. Installed as a
/// service -- the way Company Apps installs it -- the service keeps that file under
/// <c>%ProgramData%\RustDesk</c>; a per-user run keeps it under
/// <c>%APPDATA%\RustDesk</c>. ProgramData is checked first, because that is where the
/// service this platform manages keeps it.
/// </para>
/// <para>
/// The actual parsing lives in the platform-neutral <see cref="RustDeskConfig"/> so
/// it is unit-testable without Windows or a RustDesk install; this type contributes
/// only the file locations and the read.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsRemoteSupportReader(ILogger<WindowsRemoteSupportReader> logger) : IRemoteSupportReader
{
    private readonly ILogger<WindowsRemoteSupportReader> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public string? ReadRustDeskId()
    {
        foreach (var path in CandidateConfigPaths())
        {
            var text = TryReadAllText(path);
            if (text is null)
            {
                continue;
            }

            var id = RustDeskConfig.ParseDeviceId(text);
            if (!string.IsNullOrEmpty(id))
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>The two RustDesk2.toml locations, service install first, in read order.</summary>
    private static IEnumerable<string> CandidateConfigPaths()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrEmpty(programData))
        {
            yield return Path.Combine(programData, "RustDesk", "config", "RustDesk2.toml");
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            yield return Path.Combine(appData, "RustDesk", "config", "RustDesk2.toml");
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
            // Null means "no ID this heartbeat", the safe reading: the ID is not a
            // secret and an unreadable file must never disrupt the heartbeat.
            _logger.LogDebug(ex, "Could not read the RustDesk config at {Path}; reporting no ID.", path);
            return null;
        }
    }
}
