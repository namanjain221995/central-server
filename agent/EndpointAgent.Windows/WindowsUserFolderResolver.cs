using EndpointAgent.Core.FileCollection;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Windows;

/// <summary>
/// Resolves a Windows user's Desktop, Downloads and Documents folders to real local
/// directories under that user's own profile.
/// </summary>
/// <remarks>
/// <para>
/// The profile root comes from the authoritative ProfileList registry
/// (<see cref="WindowsUserHives.ProfilePath"/>), and each folder is reached through
/// <see cref="RealDirectories.Chain"/>, which walks only real directories and refuses
/// a reparse point (a junction or symlink) at any step. So a folder can never resolve
/// outside the user's profile, into a system location, or into another user's files.
/// </para>
/// <para>
/// This uses the default folder names under the profile. A user who has redirected a
/// known folder elsewhere (for example OneDrive-backed Documents) is not followed —
/// a deliberate v1 limitation, erring toward staying inside the profile.
/// </para>
/// </remarks>
public sealed class WindowsUserFolderResolver(ILogger<WindowsUserFolderResolver> logger) : IUserFolderResolver
{
    private readonly ILogger<WindowsUserFolderResolver> _logger = logger;

    public IReadOnlyList<ResolvedFolder> Resolve(string userSid, IReadOnlyList<string> rootKeys)
    {
        var resolved = new List<ResolvedFolder>();

        if (string.IsNullOrWhiteSpace(userSid) || !WindowsUserHives.IsRealUserSid(userSid))
        {
            _logger.LogWarning("File collection: {UserSid} is not a real user SID.", userSid);
            return resolved;
        }

        var profile = WindowsUserHives.ProfilePath(userSid);
        if (string.IsNullOrWhiteSpace(profile))
        {
            _logger.LogWarning("File collection: no profile path for {UserSid}.", userSid);
            return resolved;
        }

        foreach (var key in rootKeys)
        {
            // The well-known folder key IS the default subfolder name under the profile.
            var path = RealDirectories.Chain(_logger, profile, key);
            if (path is not null)
            {
                resolved.Add(new ResolvedFolder(key, path));
            }
            else
            {
                _logger.LogDebug("File collection: {Key} did not resolve under {Profile}.", key, profile);
            }
        }

        return resolved;
    }
}
