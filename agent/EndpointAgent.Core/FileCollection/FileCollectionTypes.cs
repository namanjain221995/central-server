namespace EndpointAgent.Core.FileCollection;

/// <summary>
/// One queued file collection: zip a Windows user's chosen folders, encrypt the
/// archive to the browser's public key, and upload it under this transfer id.
/// </summary>
/// <param name="TransferId">Correlates the upload with the operator's download stream.</param>
/// <param name="RecipientPublicKeyBase64">The browser's one-time ECDH P-256 public key (base64 raw point).</param>
/// <param name="UserSid">Whose profile folders to collect.</param>
/// <param name="RootKeys">Which folders: a subset of Desktop/Downloads/Documents.</param>
/// <param name="MaxBytes">Hard ceiling on the archive; past it the collection is truncated.</param>
public sealed record CollectFilesJob(
    string TransferId,
    string RecipientPublicKeyBase64,
    string UserSid,
    IReadOnlyList<string> RootKeys,
    long MaxBytes);

/// <summary>One resolved folder to include in the archive.</summary>
/// <param name="Key">The well-known name (Desktop/Downloads/Documents) — also the archive's top-level folder.</param>
/// <param name="Path">The absolute, real local directory on the endpoint.</param>
public sealed record ResolvedFolder(string Key, string Path);

/// <summary>
/// Resolves a Windows user's well-known folders to real local directories. The
/// implementation refuses anything outside the user's own profile, so a collection
/// can never reach a system location or another user's files.
/// </summary>
public interface IUserFolderResolver
{
    /// <summary>
    /// The real directories for the requested folder keys under the given user's
    /// profile. Folders that do not exist, cannot be read, or would resolve outside
    /// the profile are simply omitted.
    /// </summary>
    IReadOnlyList<ResolvedFolder> Resolve(string userSid, IReadOnlyList<string> rootKeys);
}
