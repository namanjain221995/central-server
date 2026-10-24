using System.Runtime.Versioning;
using System.Security;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Windows;

/// <summary>
/// Walks directories under a trusted root only when every step is a real
/// directory, never a reparse point.
/// </summary>
/// <remarks>
/// <para>
/// One rule, shared by everything that reads or writes inside a user's profile as
/// LocalSystem. It began life inside <see cref="WindowsChromeCollector"/> and was
/// extracted when the Chrome profile remover needed to resolve the same
/// <c>User Data</c> directory the collector resolves: two copies of a junction
/// defence would be two places for it to drift apart.
/// </para>
/// <para>
/// <b>Why every segment, not just the leaf.</b> The root is the profile directory
/// from HKLM's profile list and is the machine's to say; everything below it is
/// the user's, and a directory junction needs no privilege to plant. Every file
/// API follows a junction wherever it sits in a path, so a check on the leaf
/// alone would pass a leaf that is a perfectly real directory inside a target of
/// the user's choosing: another user's <c>User Data</c>, say, reached as
/// LocalSystem on the planter's behalf. Nothing under a profile may lead the
/// service outside the profile -- and for the remover, which deletes, the stakes
/// are higher still.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class RealDirectories
{
    /// <summary>
    /// The directory at the end of a chain of segments under a trusted root, or
    /// null when any segment is missing, is a reparse point, or cannot be
    /// inspected.
    /// </summary>
    public static string? Chain(ILogger logger, string root, params string[] segments)
    {
        var path = root;
        foreach (var segment in segments)
        {
            path = Path.Combine(path, segment);
            if (!IsReal(logger, path))
            {
                return null;
            }
        }

        return path;
    }

    /// <summary>
    /// Whether a directory exists and is a real directory rather than a junction
    /// or symbolic link. A reparse point where a directory should be is not
    /// followed, for the reason in the class remarks.
    /// </summary>
    public static bool IsReal(ILogger logger, string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            if (!directory.Exists)
            {
                return false;
            }

            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                logger.LogDebug("{Path} is a reparse point and is not read.", path);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            logger.LogDebug(ex, "Could not inspect directory {Path}.", path);
            return false;
        }
    }
}
