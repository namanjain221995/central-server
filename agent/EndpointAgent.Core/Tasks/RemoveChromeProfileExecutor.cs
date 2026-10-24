using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Inventory.Chrome;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Tasks;

/// <summary>
/// Removes one Chrome profile from one Windows user's Chrome, for a
/// <c>RemoveChromeProfile</c> task (the server's <c>DeviceTaskType</c> value 62;
/// the dispatcher routes by the name): the <c>profile.info_cache</c> entry comes
/// out of <c>Local State</c> first, then the profile directory is deleted.
/// </summary>
/// <remarks>
/// <para>
/// <b>Local State first, directory second, on purpose.</b> Chrome's UI shows the
/// profiles <c>info_cache</c> lists, so with the entry gone a half-done removal
/// is a profile Chrome has already forgotten -- an orphaned directory a retry
/// finishes deleting -- whereas the other order would leave Chrome offering a
/// profile whose directory is gone. A directory delete that fails after the edit
/// succeeded is reported as partial, with the exact remaining path, and the task
/// is safe to re-run: an entry already absent is not refused, it is the state a
/// partial run leaves behind (and a fully-done task re-delivered reports success
/// with nothing to do).
/// </para>
/// <para>
/// <b>Every gate runs before anything is written.</b> The payload's profile key
/// must pass the same plain-directory-name rule the Local State parser applies
/// (<see cref="ChromeLocalState.IsProfileKey"/>); "Default" is refused by policy;
/// the path this device computes for the key must equal the payload's
/// <c>profilePath</c>, so a payload built against another device, or against
/// a profile list that has since changed shape, removes nothing; the entry's
/// <c>user_name</c> must match the console's <c>expectedEmail</c> when one was
/// sent, so a stale inventory can never direct the delete at a profile that has
/// since changed hands; and Chrome must not be running for that user, or the
/// rewrite would race the running browser (which rewrites <c>Local State</c> from
/// memory on exit and would resurrect the entry) and the directory would be
/// locked mid-delete.
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
public sealed class RemoveChromeProfileExecutor(
    IChromeProfileRemover remover,
    ILogger<RemoveChromeProfileExecutor> logger) : ITaskExecutor
{
    /// <summary>The profile Chrome itself falls back to; never removable remotely.</summary>
    internal const string DefaultProfileKey = "Default";

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

    private readonly ILogger<RemoveChromeProfileExecutor> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public string TaskType => "RemoveChromeProfile";

    public async Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (!TryParse(task.PayloadJson, out var payload, out var refusal))
        {
            _logger.LogWarning("Chrome profile removal task {TaskId} refused: {Reason}", task.TaskId, refusal);
            return new AgentTaskResult(false, refusal, null);
        }

        // ---- resolve this device's own path for the named profile ------------

        var userData = _remover.ResolveUserDataDirectory(payload.UserSid);
        if (userData is null)
        {
            return Refused(task,
                $"The Windows account '{payload.UserSid}' could not be resolved to a Chrome "
                + "'User Data' directory on this device; nothing was removed.");
        }

        var resolvedPath = Path.Combine(userData, payload.ProfileKey);
        if (!SameLocalPath(resolvedPath, payload.ProfilePath))
        {
            // A payload whose path is not the path this device computes was built
            // against another device or an out-of-date profile list.
            return Refused(task,
                $"The payload's profilePath does not match this device's path for profile "
                + $"'{payload.ProfileKey}' ('{resolvedPath}'); the task looks stale or meant "
                + "for another device, so nothing was removed.");
        }

        // ---- the running browser owns these files ----------------------------

        if (_remover.IsChromeRunningFor(payload.UserSid))
        {
            return Refused(task, "Chrome is running for this user; close Chrome (or restart the device) and retry.");
        }

        // ---- Local State: validate the entry, then take it out ---------------

        var localStateJson = _remover.ReadLocalState(userData);
        if (localStateJson is null)
        {
            return Refused(task, "Chrome's Local State file could not be read for this user; nothing was removed.");
        }

        JsonObject root;
        try
        {
            if (JsonNode.Parse(localStateJson, nodeOptions: null, LocalStateReadOptions) is not JsonObject parsed)
            {
                return Refused(task, "Chrome's Local State file did not parse as a JSON object; nothing was removed.");
            }

            root = parsed;
        }
        catch (JsonException)
        {
            return Refused(task, "Chrome's Local State file did not parse as a JSON object; nothing was removed.");
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
                return Refused(task,
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
                return Refused(task,
                    $"Chrome's profile list could not be rewritten ({ex.Message}); nothing was removed.");
            }
        }

        // ---- the directory, now that Chrome has forgotten the profile --------

        var emailLabel = accountEmail ?? "(no account)";

        if (!_remover.ProfileDirectoryExists(userData, payload.ProfileKey))
        {
            if (!localStateChanged)
            {
                return Done(task, localStateUpdated: false, payload.ProfileKey,
                    $"Chrome profile '{payload.ProfileKey}' ({emailLabel}) was already removed: "
                    + "Chrome's profile list does not carry it and its directory is gone. Nothing needed doing.");
            }

            return Done(task, localStateUpdated: true, payload.ProfileKey,
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
            // what an operator (or a retry of this very task) needs next.
            var message =
                $"Chrome profile '{payload.ProfileKey}' ({emailLabel}) was removed from Chrome's profile "
                + $"list, but its directory could not be deleted ({ex.Message}). Remaining path: {resolvedPath}";
            _logger.LogWarning(ex, "Chrome profile removal task {TaskId} partial: {Message}", task.TaskId, message);

            return new AgentTaskResult(false, message,
                BuildResultJson(payload.ProfileKey, localStateUpdated: localStateChanged, directoryDeleted: false, resolvedPath));
        }

        var removed = localStateChanged
            ? $"Removed Chrome profile '{payload.ProfileKey}' ({emailLabel}): Chrome's profile list no "
              + "longer carries it and its directory was deleted. No other profile was touched."
            : $"Removed Chrome profile '{payload.ProfileKey}' ({emailLabel}): Chrome's profile list no "
              + "longer carried it, and its leftover directory was deleted. No other profile was touched.";

        return Done(task, localStateChanged, payload.ProfileKey, removed);
    }

    private AgentTaskResult Refused(AgentTask task, string reason)
    {
        _logger.LogWarning("Chrome profile removal task {TaskId} refused: {Reason}", task.TaskId, reason);
        return new AgentTaskResult(false, reason, null);
    }

    private AgentTaskResult Done(AgentTask task, bool localStateUpdated, string profileKey, string message)
    {
        _logger.LogInformation("Chrome profile removal task {TaskId}: {Message}", task.TaskId, message);
        return new AgentTaskResult(true, message,
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

    /// <summary>
    /// Reads the payload. Strict about what it acts on: <c>userSid</c>,
    /// <c>profileKey</c> and <c>profilePath</c> must be usable strings, the key
    /// must be a plain directory name by the parser's own rule and must not be
    /// "Default", and <c>expectedEmail</c> must be a string or null. Unknown extra
    /// properties are tolerated and ignored -- the payload arrives over the
    /// authenticated task channel, and a newer server adding a field must not
    /// strand older agents.
    /// </summary>
    internal static bool TryParse(
        string? payloadJson,
        out RemoveChromeProfilePayload payload,
        [NotNullWhen(false)] out string? refusal)
    {
        payload = default;

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            refusal = "The Chrome profile removal task carried no payload.";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payloadJson);
        }
        catch (JsonException)
        {
            refusal = "The Chrome profile removal payload was not valid JSON.";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                refusal = "The Chrome profile removal payload was not a JSON object.";
                return false;
            }

            if (ReadRequiredString(root, "userSid") is not { } userSid)
            {
                refusal = "The Chrome profile removal payload carried no usable 'userSid'.";
                return false;
            }

            if (ReadRequiredString(root, "profileKey") is not { } profileKey
                || !ChromeLocalState.IsProfileKey(profileKey))
            {
                refusal = "The Chrome profile removal payload carried no usable 'profileKey'; "
                    + "a profile key is a plain directory name under Chrome's User Data.";
                return false;
            }

            if (string.Equals(profileKey, DefaultProfileKey, StringComparison.OrdinalIgnoreCase))
            {
                refusal = "Removing the 'Default' Chrome profile is refused by policy; only secondary "
                    + "profiles ('Profile 1', 'Profile 2', ...) can be removed remotely.";
                return false;
            }

            if (ReadRequiredString(root, "profilePath") is not { } profilePath)
            {
                refusal = "The Chrome profile removal payload carried no usable 'profilePath'.";
                return false;
            }

            string? expectedEmail = null;
            if (root.TryGetProperty("expectedEmail", out var emailElement)
                && emailElement.ValueKind != JsonValueKind.Null)
            {
                // Null and absent both mean "the console saw no account"; any
                // other non-string is a payload this agent does not understand,
                // refused rather than read as no check.
                if (emailElement.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(emailElement.GetString()))
                {
                    refusal = "The Chrome profile removal payload carried an unusable 'expectedEmail'; "
                        + "expected an account address or null.";
                    return false;
                }

                expectedEmail = emailElement.GetString()!.Trim();
            }

            payload = new RemoveChromeProfilePayload(userSid, profileKey, expectedEmail, profilePath);
            refusal = null;
            return true;
        }
    }

    private static string? ReadRequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>The validated payload of one <c>RemoveChromeProfile</c> task.</summary>
    internal readonly record struct RemoveChromeProfilePayload(
        string UserSid,
        string ProfileKey,
        string? ExpectedEmail,
        string ProfilePath);
}
