namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// The Windows facilities the <c>RemoveChromeProfile</c> executor needs: locating
/// one user's Chrome <c>User Data</c> directory, knowing whether Chrome is running
/// for that user, and the file operations on exactly one profile.
/// </summary>
/// <remarks>
/// <para>
/// The seam is cut here, below the decisions, on purpose: everything that decides
/// whether a profile may be removed -- payload validation, the Default refusal,
/// the path and account checks, the <c>Local State</c> rewrite, the ordering of
/// edit before delete and the partial-failure reporting -- lives in the removal
/// engine (<c>ChromeProfileRemovalEngine</c>, shared by the task executor and the
/// deferred-removal worker) and is pinned with fakes, while this interface
/// carries only what needs a real machine (the ProfileList registry, process
/// tokens, NTFS).
/// </para>
/// <para>
/// Both directory operations take the <c>User Data</c> directory and the profile
/// key separately rather than a joined path, so the one place that joins them is
/// the Windows implementation, which re-applies the collector's plain-directory-
/// name rule (<c>WindowsChromeCollector.IsPlainProfileKey</c>) before any join.
/// A payload can therefore never steer a delete outside <c>User Data</c>, even if
/// a bug upstream let an odd key through.
/// </para>
/// </remarks>
public interface IChromeProfileRemover
{
    /// <summary>
    /// The user's Chrome <c>User Data</c> directory, resolved from their SID the
    /// way the inventory collector resolves it (the machine's profile list, then
    /// a chain of real directories -- never through a reparse point). Null when
    /// the SID is not a user's, the machine does not know the profile, or the
    /// directory is not there to be resolved.
    /// </summary>
    string? ResolveUserDataDirectory(string userSid);

    /// <summary>
    /// Whether any <c>chrome.exe</c> process is running under the given user's
    /// account, read from each process token. Chrome running for anyone else does
    /// not count: their browser is not touched by removing this user's profile.
    /// </summary>
    bool IsChromeRunningFor(string userSid);

    /// <summary>
    /// The text of the user's <c>Local State</c> file, or null when it is absent,
    /// oversized, a reparse point, or unreadable this instant.
    /// </summary>
    string? ReadLocalState(string userDataDirectory);

    /// <summary>
    /// Replaces the user's <c>Local State</c> with <paramref name="json"/>, as
    /// atomically as the file system allows (a temporary file swapped over the
    /// old one). Throws on failure; the executor decides what a failure means.
    /// </summary>
    void WriteLocalState(string userDataDirectory, string json);

    /// <summary>
    /// Whether <c><paramref name="userDataDirectory"/>\<paramref name="profileKey"/></c>
    /// exists as a real directory (a reparse point in its place is not a profile
    /// directory and reads as absent -- it is never followed).
    /// </summary>
    bool ProfileDirectoryExists(string userDataDirectory, string profileKey);

    /// <summary>
    /// Recursively deletes
    /// <c><paramref name="userDataDirectory"/>\<paramref name="profileKey"/></c>.
    /// A directory already gone is success; a reparse point in its place is a
    /// failure, never a traversal. Throws on failure.
    /// </summary>
    void DeleteProfileDirectory(string userDataDirectory, string profileKey);
}
