using System.Text.Json;
using System.Text.Json.Nodes;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Inventory.Chrome;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.ChromeProfiles;

/// <summary>The validated request one Chrome profile removal acts on.</summary>
public readonly record struct RemoveChromeProfilePayload(
    string UserSid,
    string ProfileKey,
    string? ExpectedEmail,
    string ProfilePath);

/// <summary>How one run of the removal engine ended.</summary>
public enum ChromeProfileRemovalDisposition
{
    /// <summary>
    /// The profile is gone: removed by this run, or found already removed (the
    /// idempotent re-run). Terminal success.
    /// </summary>
    Removed,

    /// <summary>
    /// Every stale/wrong-request gate passed and the one thing between this
    /// request and the removal is that Chrome is running for the user right now.
    /// Nothing was touched. The ONLY deferrable outcome: the caller may park the
    /// request and re-run it once Chrome has closed.
    /// </summary>
    ChromeRunning,

    /// <summary>
    /// A hard refusal: the request is stale, wrong, or unactionable on this
    /// device. Nothing was removed, and the request must not be retried as it
    /// stands. Terminal.
    /// </summary>
    Refused,

    /// <summary>
    /// The profile list was rewritten but the directory could not be deleted;
    /// re-running the engine finishes the job.
    /// </summary>
    PartiallyRemoved,
}

/// <summary>
/// What one engine run decided and did: the disposition, the exact message the
/// task channel (or the log) carries, and -- for the outcomes that did or partly
/// did the work -- the structured result the server parses.
/// </summary>
public sealed record ChromeProfileRemovalOutcome(
    ChromeProfileRemovalDisposition Disposition,
    string Message,
    string? ResultJson);

/// <summary>
/// The one Chrome-profile-removal engine: validates a request against this
/// device and, when Chrome is not in the way, removes the profile -- the
/// <c>profile.info_cache</c> entry out of <c>Local State</c> first, then the
/// profile directory.
/// </summary>
/// <remarks>
/// <para>
/// <b>One engine, two callers, on purpose.</b> <c>RemoveChromeProfileExecutor</c>
/// runs it when the task arrives; <see cref="ChromeProfileRemovalWorker"/>
/// re-runs it for requests that were deferred because Chrome was open. Both go
/// through this one method, so a deferred request faces exactly the gates a
/// fresh task faces -- there is no second, weaker path a stale request could
/// slip through later.
/// </para>
/// <para>
/// <b>Every stale/wrong gate runs before the Chrome gate.</b> The payload's key
/// must be a plain directory name and not "Default"; the path this device
/// computes for the key must equal the request's <c>profilePath</c>; and the
/// entry's <c>user_name</c> must match the console's <c>expectedEmail</c> when
/// one was sent. All of these are read-only and are checked FIRST, so a request
/// that is wrong for this device fails immediately -- it never waits, scheduled,
/// for Chrome to close. Only a request that passed them all may report
/// <see cref="ChromeProfileRemovalDisposition.ChromeRunning"/>, and the Chrome
/// gate is the last gate before anything is written: the rewrite would race the
/// running browser (which rewrites <c>Local State</c> from memory on exit and
/// would resurrect the entry) and the directory would be locked mid-delete.
/// </para>
/// <para>
/// <b>Local State first, directory second, on purpose.</b> Chrome's UI shows the
/// profiles <c>info_cache</c> lists, so with the entry gone a half-done removal
/// is a profile Chrome has already forgotten -- an orphaned directory a re-run
/// finishes deleting -- whereas the other order would leave Chrome offering a
/// profile whose directory is gone. A directory delete that fails after the edit
/// succeeded is reported as <see cref="ChromeProfileRemovalDisposition.PartiallyRemoved"/>,
/// with the exact remaining path, and a re-run is safe: an entry already absent
/// is not refused, it is the state a partial run leaves behind (and a removal
/// already complete reports success with nothing to do). Note one consequence
/// for the deferred path: while Chrome is RUNNING, an entry absent on disk
/// proves nothing -- Chrome rewrites <c>Local State</c> from memory on exit and
/// may resurrect it -- so even an apparently-done removal is still answered
/// with <c>ChromeRunning</c> rather than declared complete, and the re-run
/// after Chrome closes settles it.
/// </para>
/// <para>
/// <b>Only that profile's data.</b> The rewrite goes through the JSON DOM -- the
/// same System.Text.Json the parser reads with -- removing the one entry and, when
/// <c>profile.last_used</c> names the removed key, pointing it back at "Default";
/// everything else in the file is carried over as parsed. The delete is the one
/// directory the validated key names, joined and re-validated behind
/// <see cref="IChromeProfileRemover"/>. Sharing violations get one retry after a
/// short pause, because Chrome's crash-handler or an antivirus scan can hold a
/// file for a moment; anything longer-lived is reported, not fought.
/// </para>
/// </remarks>
public sealed class ChromeProfileRemovalEngine(
    IChromeProfileRemover remover,
    ILogger<ChromeProfileRemovalEngine> logger)
{
    /// <summary>The profile Chrome itself falls back to; never removable remotely.</summary>
    internal const string DefaultProfileKey = "Default";

    /// <summary>The exact text a <c>ChromeRunning</c> outcome carries; what the refusal used to say, kept for the logs.</summary>
    internal const string ChromeRunningMessage =
        "Chrome is running for this user; close Chrome (or restart the device) and retry.";

    /// <summary>
    /// The pause before the one retry a sharing violation gets. Long enough for a
    /// momentary scanner handle to go, short enough not to stall the task loop.
    /// </summary>
    internal static readonly TimeSpan SharingViolationRetryDelay = TimeSpan.FromMilliseconds(250);

    // The same reading tolerances the Local State parser uses: Chrome writes
    // neither trailing commas nor comments, but refusing a hand-edited file over
    // one helps no one, and the depth bound stops a crafted file from recursing.
    private static readonly JsonDocumentOptions LocalStateReadOptions = new()
    {
        MaxDepth = 64,
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly IChromeProfileRemover _remover = remover
        ?? throw new ArgumentNullException(nameof(remover));

    private readonly ILogger<ChromeProfileRemovalEngine> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Validates <paramref name="payload"/> against this device and removes the
    /// profile when nothing stands in the way. <paramref name="taskId"/> is the
    /// originating task, carried into every log line so a removal that completes
    /// minutes or days after its task (the deferred path) can still be read back
    /// to the click that asked for it.
    /// </summary>
    public async Task<ChromeProfileRemovalOutcome> RunAsync(
        Guid taskId, RemoveChromeProfilePayload payload, CancellationToken cancellationToken = default)
    {
        // The two key gates the task parser also applies, re-checked here so a
        // request arriving by the deferred path (the pending store) faces them
        // too. Unreachable from the task path, which refused them at parse.
        if (!ChromeLocalState.IsProfileKey(payload.ProfileKey))
        {
            return Refused(taskId,
                "The Chrome profile removal payload carried no usable 'profileKey'; "
                + "a profile key is a plain directory name under Chrome's User Data.");
        }

        if (string.Equals(payload.ProfileKey, DefaultProfileKey, StringComparison.OrdinalIgnoreCase))
        {
            return Refused(taskId,
                "Removing the 'Default' Chrome profile is refused by policy; only secondary "
                + "profiles ('Profile 1', 'Profile 2', ...) can be removed remotely.");
        }

        // ---- resolve this device's own path for the named profile ------------

        var userData = _remover.ResolveUserDataDirectory(payload.UserSid);
        if (userData is null)
        {
            return Refused(taskId,
                $"The Windows account '{payload.UserSid}' could not be resolved to a Chrome "
                + "'User Data' directory on this device; nothing was removed.");
        }

        var resolvedPath = Path.Combine(userData, payload.ProfileKey);
        if (!SameLocalPath(resolvedPath, payload.ProfilePath))
        {
            // A payload whose path is not the path this device computes was built
            // against another device or an out-of-date profile list.
            return Refused(taskId,
                $"The payload's profilePath does not match this device's path for profile "
                + $"'{payload.ProfileKey}' ('{resolvedPath}'); the task looks stale or meant "
                + "for another device, so nothing was removed.");
        }

        // ---- Local State: validate the entry before anything may defer -------
        // Read-only, and deliberately BEFORE the Chrome-running gate: a request
        // the console built against a view that has since changed must fail now,
        // never sit scheduled waiting for Chrome to close.

        var localStateJson = _remover.ReadLocalState(userData);
        if (localStateJson is null)
        {
            return Refused(taskId, "Chrome's Local State file could not be read for this user; nothing was removed.");
        }

        JsonObject root;
        try
        {
            if (JsonNode.Parse(localStateJson, nodeOptions: null, LocalStateReadOptions) is not JsonObject parsed)
            {
                return Refused(taskId, "Chrome's Local State file did not parse as a JSON object; nothing was removed.");
            }

            root = parsed;
        }
        catch (JsonException)
        {
            return Refused(taskId, "Chrome's Local State file did not parse as a JSON object; nothing was removed.");
        }

        var profileSection = root["profile"] as JsonObject;
        var infoCache = profileSection?["info_cache"] as JsonObject;

        // The key names a directory, and on Windows two spellings of one name are
        // one directory -- the parser's rule -- so every spelling of the key is
        // the one profile and all of them come out. The first entry is the one
        // whose account is checked, matching the parser's first-entry-wins.
        List<string> matchingKeys = infoCache is null
            ? []
            : infoCache.Where(p => string.Equals(p.Key, payload.ProfileKey, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Key)
                .ToList();

        string? accountEmail;
        if (matchingKeys.Count > 0)
        {
            var entry = infoCache![matchingKeys[0]] as JsonObject;
            accountEmail = ReadEntryString(entry, "user_name");

            // The stale-inventory defence: the console decided on an inventory
            // snapshot, and if the profile has changed hands since, this is not
            // the profile the administrator meant.
            if (payload.ExpectedEmail is not null
                && !string.Equals(accountEmail, payload.ExpectedEmail, StringComparison.OrdinalIgnoreCase))
            {
                return Refused(taskId,
                    "The profile's signed-in account no longer matches the console's view; "
                    + "refresh Chrome inventory and retry.");
            }
        }
        else
        {
            // Chrome no longer lists the profile: the state a partial run (edit
            // done, delete failed) leaves behind, so it must be continuable, and
            // there is no listed account left to check a stale console view
            // against -- only an orphaned directory, or nothing at all.
            accountEmail = payload.ExpectedEmail;
        }

        // ---- the running browser owns these files ----------------------------
        // The LAST gate, after every stale/wrong check and before anything is
        // written: the one blocker that clears itself, and so the one outcome a
        // caller may defer on instead of failing.

        if (_remover.IsChromeRunningFor(payload.UserSid))
        {
            return new ChromeProfileRemovalOutcome(
                ChromeProfileRemovalDisposition.ChromeRunning, ChromeRunningMessage, null);
        }

        // ---- Local State: take the entry out ----------------------------------

        var localStateChanged = false;
        foreach (var keySpelling in matchingKeys)
        {
            infoCache!.Remove(keySpelling);
            localStateChanged = true;
        }

        // A last_used naming the removed profile would greet the next launch with
        // a profile Chrome cannot open; "Default" always exists to Chrome.
        if (profileSection?["last_used"] is JsonValue lastUsed
            && lastUsed.TryGetValue<string>(out var lastUsedKey)
            && string.Equals(lastUsedKey, payload.ProfileKey, StringComparison.OrdinalIgnoreCase))
        {
            profileSection["last_used"] = DefaultProfileKey;
            localStateChanged = true;
        }

        if (localStateChanged)
        {
            try
            {
                await WithOneRetryOnSharingViolation(
                    () => _remover.WriteLocalState(userData, root.ToJsonString()), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Nothing was deleted: the directory is only ever touched after
                // the profile list no longer carries the entry.
                return Refused(taskId,
                    $"Chrome's profile list could not be rewritten ({ex.Message}); nothing was removed.");
            }
        }

        // ---- the directory, now that Chrome has forgotten the profile --------

        var emailLabel = accountEmail ?? "(no account)";

        if (!_remover.ProfileDirectoryExists(userData, payload.ProfileKey))
        {
            if (!localStateChanged)
            {
                return Done(taskId, localStateUpdated: false, payload.ProfileKey,
                    $"Chrome profile '{payload.ProfileKey}' ({emailLabel}) was already removed: "
                    + "Chrome's profile list does not carry it and its directory is gone. Nothing needed doing.");
            }

            return Done(taskId, localStateUpdated: true, payload.ProfileKey,
                $"Removed Chrome profile '{payload.ProfileKey}' ({emailLabel}): Chrome's profile list "
                + "no longer carries it; its directory was already gone. No other profile was touched.");
        }

        try
        {
            await WithOneRetryOnSharingViolation(
                () => _remover.DeleteProfileDirectory(userData, payload.ProfileKey), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Partial: the list is clean, the directory is not. The exact path is
            // what an operator (or a re-run of this very request) needs next.
            var message =
                $"Chrome profile '{payload.ProfileKey}' ({emailLabel}) was removed from Chrome's profile "
                + $"list, but its directory could not be deleted ({ex.Message}). Remaining path: {resolvedPath}";
            _logger.LogWarning(ex, "Chrome profile removal task {TaskId} partial: {Message}", taskId, message);

            return new ChromeProfileRemovalOutcome(
                ChromeProfileRemovalDisposition.PartiallyRemoved, message,
                BuildResultJson(payload.ProfileKey, localStateUpdated: localStateChanged, directoryDeleted: false, resolvedPath));
        }

        var removed = localStateChanged
            ? $"Removed Chrome profile '{payload.ProfileKey}' ({emailLabel}): Chrome's profile list no "
              + "longer carries it and its directory was deleted. No other profile was touched."
            : $"Removed Chrome profile '{payload.ProfileKey}' ({emailLabel}): Chrome's profile list no "
              + "longer carried it, and its leftover directory was deleted. No other profile was touched.";

        return Done(taskId, localStateChanged, payload.ProfileKey, removed);
    }

    private ChromeProfileRemovalOutcome Refused(Guid taskId, string reason)
    {
        _logger.LogWarning("Chrome profile removal task {TaskId} refused: {Reason}", taskId, reason);
        return new ChromeProfileRemovalOutcome(ChromeProfileRemovalDisposition.Refused, reason, null);
    }

    private ChromeProfileRemovalOutcome Done(Guid taskId, bool localStateUpdated, string profileKey, string message)
    {
        _logger.LogInformation("Chrome profile removal task {TaskId}: {Message}", taskId, message);
        return new ChromeProfileRemovalOutcome(ChromeProfileRemovalDisposition.Removed, message,
            BuildResultJson(profileKey, localStateUpdated, directoryDeleted: true, remainingPath: null));
    }

    /// <summary>
    /// The structured result the server parses: which profile, whether this run
    /// rewrote the profile list, and whether the directory is gone now (deleted,
    /// or found already gone). <paramref name="remainingPath"/> is carried only
    /// by the partial outcome.
    /// </summary>
    internal static string BuildResultJson(string profileKey, bool localStateUpdated, bool directoryDeleted, string? remainingPath) =>
        JsonSerializer.Serialize(new { profileKey, localStateUpdated, directoryDeleted, remainingPath });

    /// <summary>
    /// Runs one file operation, retrying exactly once after a short pause when it
    /// fails with a Windows sharing or lock violation -- the transient hold a
    /// scanner or crash-handler keeps for a moment. Any other failure, or a second
    /// one, is the caller's to report.
    /// </summary>
    private static async Task WithOneRetryOnSharingViolation(Action operation, CancellationToken cancellationToken)
    {
        try
        {
            operation();
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            await Task.Delay(SharingViolationRetryDelay, cancellationToken);
            operation();
        }
    }

    /// <summary>
    /// Whether an I/O failure is ERROR_SHARING_VIOLATION (32) or
    /// ERROR_LOCK_VIOLATION (33), read from the Win32 code in the low word of the
    /// HResult -- the one kind of failure worth a second try.
    /// </summary>
    internal static bool IsSharingViolation(IOException exception) =>
        (exception.HResult & 0xFFFF) is 32 or 33;

    /// <summary>
    /// Whether two local paths name the same thing, the way this agent's Windows
    /// file system does: ordinal, case-insensitive, indifferent to a trailing
    /// separator and to which separator was written.
    /// </summary>
    private static bool SameLocalPath(string left, string right) =>
        string.Equals(NormalizeLocalPath(left), NormalizeLocalPath(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeLocalPath(string path) =>
        path.Replace('/', '\\').TrimEnd('\\');

    /// <summary>A string field of an info cache entry; null when absent, not a string, or blank.</summary>
    private static string? ReadEntryString(JsonObject? entry, string name)
    {
        if (entry?[name] is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
