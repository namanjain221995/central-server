using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;
using System.Text;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Inventory.Chrome;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace EndpointAgent.Windows;

/// <summary>
/// The Windows half of Chrome profile removal: resolves one user's
/// <c>User Data</c> directory the way the inventory collector resolves it, tells
/// whether Chrome is running for that user, and performs the two file operations
/// the executor orders -- the <c>Local State</c> swap and the profile directory
/// delete.
/// </summary>
/// <remarks>
/// <para>
/// <b>No decision lives here.</b> Which profile may be removed, in what order the
/// operations run, and what a failure means are
/// <c>ChromeProfileRemovalEngine</c>'s, pinned with fakes; this class only
/// answers for what needs a real machine. It is registered separately from the
/// read-only <see cref="Core.Abstractions.IChromeCollector"/>, so inventory stays
/// incapable of deletion and deletion incapable of inventory.
/// </para>
/// <para>
/// <b>Inside the user's own profile, and nowhere else.</b> The profile root comes
/// from HKLM's profile list; every directory from it down to <c>User Data</c>
/// must be a real directory (<see cref="RealDirectories"/>), a profile key is
/// re-checked to be a plain directory name
/// (<see cref="WindowsChromeCollector.IsPlainProfileKey"/>) before any path join,
/// and a reparse point where the profile directory should be is never followed --
/// it reads as absent and is refused for deletion, so a planted junction cannot
/// make the service delete, or even enumerate, anything outside the profile.
/// </para>
/// <para>
/// <b>Process inspection, not process control (ADR-0005).</b> The Chrome-running
/// check enumerates <c>chrome.exe</c> processes and reads each one's token user
/// SID -- <c>OpenProcess</c> for query access, <c>OpenProcessToken</c> with
/// TOKEN_QUERY, <c>GetTokenInformation</c> -- and nothing more: no process is
/// started (the launch vector the ADR bans), stopped or signalled. A process that
/// cannot be inspected is skipped: it cannot be shown to be this user's, and a
/// delete that races an uninspected Chrome still fails safe, as a sharing
/// violation the executor reports.
/// </para>
/// <para>
/// <b>The write Chrome's own writer would recognise.</b> Chrome replaces
/// <c>Local State</c> atomically -- a temporary file renamed over the old one --
/// and so does this: UTF-8 without a byte order mark to a sibling temporary file,
/// then <see cref="File.Replace(string, string, string?, bool)"/>. A reader
/// racing the swap sees the old document or the new one, never half of either.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsChromeProfileRemover(ILogger<WindowsChromeProfileRemover> logger) : IChromeProfileRemover
{
    private const string LocalStateFile = "Local State";

    /// <summary>Chrome's image name as the process table carries it (no extension).</summary>
    private const string ChromeProcessName = "chrome";

    /// <summary>The suffix of the temporary sibling the Local State swap goes through.</summary>
    internal const string RewriteSuffix = ".epp-rewrite";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ILogger<WindowsChromeProfileRemover> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public string? ResolveUserDataDirectory(string userSid)
    {
        if (string.IsNullOrWhiteSpace(userSid) || !WindowsUserHives.IsRealUserSid(userSid))
        {
            return null;
        }

        var profile = WindowsUserHives.ProfilePath(userSid);
        if (profile is null)
        {
            return null;
        }

        // The collector's exact walk: every step a real directory, never a
        // reparse point (RealDirectories holds the shared rule and the reasons).
        return RealDirectories.Chain(_logger, profile, "AppData", "Local", "Google", "Chrome", "User Data");
    }

    /// <inheritdoc />
    public bool IsChromeRunningFor(string userSid)
    {
        foreach (var process in Process.GetProcessesByName(ChromeProcessName))
        {
            using (process)
            {
                string? owner;
                try
                {
                    owner = ProcessOwnerSid(process.Id);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    _logger.LogDebug(ex, "Could not inspect the owner of chrome.exe process {Pid}; skipping it.", process.Id);
                    continue;
                }

                if (owner is not null && string.Equals(owner, userSid, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("chrome.exe process {Pid} is running for {Sid}.", process.Id, userSid);
                    return true;
                }
            }
        }

        return false;
    }

    /// <inheritdoc />
    public string? ReadLocalState(string userDataDirectory)
    {
        var path = Path.Combine(userDataDirectory, LocalStateFile);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            // A file that is a link is not something Chrome writes; the same
            // reasoning as the collector's for every file it opens.
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                _logger.LogDebug("{Path} is a reparse point and is not read.", path);
                return null;
            }

            if (info.Length > ChromeLocalState.MaxBytes)
            {
                _logger.LogDebug("{Path} is {Length} bytes, over the {Max}-byte cap, and is not read.", path, info.Length, ChromeLocalState.MaxBytes);
                return null;
            }

            // The same sharing the collector reads with, so Chrome's own atomic
            // replace is never blocked by this handle; and the same bounded read,
            // because the length above is a claim the user's file need not keep.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return BoundedReadText(stream, ChromeLocalState.MaxBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            _logger.LogDebug(ex, "Could not read {Path}.", path);
            return null;
        }
    }

    /// <inheritdoc />
    public void WriteLocalState(string userDataDirectory, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var path = Path.Combine(userDataDirectory, LocalStateFile);
        var temporary = path + RewriteSuffix;

        // Write the whole new document beside the old one, then swap. UTF-8
        // without a byte order mark is what Chrome itself writes.
        File.WriteAllText(temporary, json, Utf8NoBom);
        try
        {
            File.Replace(temporary, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        catch (FileNotFoundException)
        {
            // The file vanished between the read and this swap; the new document
            // simply takes its place.
            File.Move(temporary, path);
        }
        catch
        {
            TryDeleteFile(temporary);
            throw;
        }

        _logger.LogInformation("Rewrote {Path} ({Length} characters).", path, json.Length);
    }

    /// <inheritdoc />
    public bool ProfileDirectoryExists(string userDataDirectory, string profileKey)
    {
        EnsurePlainProfileKey(profileKey);
        return RealDirectories.IsReal(_logger, Path.Combine(userDataDirectory, profileKey));
    }

    /// <inheritdoc />
    public void DeleteProfileDirectory(string userDataDirectory, string profileKey)
    {
        EnsurePlainProfileKey(profileKey);
        var path = Path.Combine(userDataDirectory, profileKey);

        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
        {
            // Already gone is the state the delete exists to reach.
            return;
        }

        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            // Never follow, and never quietly remove, a link where the profile
            // directory should be: refusing loudly is what surfaces the games.
            throw new IOException($"'{path}' is a reparse point, not a profile directory; refusing to delete it.");
        }

        Directory.Delete(path, recursive: true);
        _logger.LogInformation("Deleted Chrome profile directory {Path}.", path);
    }

    /// <summary>
    /// The guard in front of every path join: the executor already validated the
    /// key, so tripping this is a bug upstream, reported as such rather than
    /// joined into a path.
    /// </summary>
    private static void EnsurePlainProfileKey(string profileKey)
    {
        if (!WindowsChromeCollector.IsPlainProfileKey(profileKey))
        {
            throw new ArgumentException($"'{profileKey}' is not a plain profile directory name.", nameof(profileKey));
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            _logger.LogDebug(ex, "Could not remove the temporary file {Path}.", path);
        }
    }

    /// <summary>
    /// At most <paramref name="maxBytes"/> of the stream as text, with a UTF-8
    /// byte order mark stripped, or null when the stream turns out longer than it
    /// said -- the file is the user's and can grow between the length check and
    /// this read, and the cap must hold regardless.
    /// </summary>
    private static string? BoundedReadText(Stream stream, long maxBytes)
    {
        var buffer = new byte[checked((int)maxBytes) + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        if (total > maxBytes)
        {
            return null;
        }

        var offset = total >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(buffer, offset, total - offset);
    }

    /// <summary>
    /// The SID of the account a process runs as, from its token, or null when the
    /// process is gone or its token cannot be read. Query access only.
    /// </summary>
    private static string? ProcessOwnerSid(int processId)
    {
        using var process = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryLimitedInformation, inheritHandle: false, processId);
        if (process.IsInvalid)
        {
            return null;
        }

        if (!NativeMethods.OpenProcessToken(process, NativeMethods.TokenQuery, out var token))
        {
            return null;
        }

        using (token)
        {
            return TokenUserSid(token);
        }
    }

    /// <summary>The token's user SID, via GetTokenInformation(TokenUser).</summary>
    private static string? TokenUserSid(SafeAccessTokenHandle token)
    {
        // First call sizes the buffer; a size outside anything a TOKEN_USER can
        // be is refused rather than allocated.
        NativeMethods.GetTokenInformation(token, NativeMethods.TokenUserClass, IntPtr.Zero, 0, out var needed);
        if (needed <= 0 || needed > 1024)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenUserClass, buffer, needed, out _))
            {
                return null;
            }

            // TOKEN_USER is one SID_AND_ATTRIBUTES: a pointer to the SID first,
            // the attributes after. Only the SID is wanted.
            var sidPointer = Marshal.ReadIntPtr(buffer);
            return sidPointer == IntPtr.Zero ? null : new SecurityIdentifier(sidPointer).Value;
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Read-only process and token queries. Nothing here can start, stop or
    /// change a process; inspection is the whole surface.
    /// </summary>
    private static class NativeMethods
    {
        internal const uint ProcessQueryLimitedInformation = 0x1000;
        internal const uint TokenQuery = 0x0008;

        /// <summary>TOKEN_INFORMATION_CLASS.TokenUser.</summary>
        internal const int TokenUserClass = 1;

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(SafeProcessHandle processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformation(SafeAccessTokenHandle tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);
    }
}
