using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using EndpointAgent.Core.ChromeProfiles;
using EndpointAgent.Core.Inventory.Chrome;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Tasks;

/// <summary>
/// Removes one Chrome profile from one Windows user's Chrome, for a
/// <c>RemoveChromeProfile</c> task (the server's <c>DeviceTaskType</c> value 62;
/// the dispatcher routes by the name). The work itself -- every gate and both
/// file operations -- is <see cref="ChromeProfileRemovalEngine"/>'s, shared with
/// the deferred-removal worker; this executor parses the payload, runs the
/// engine once, and decides what the task channel is told.
/// </summary>
/// <remarks>
/// <para>
/// <b>Chrome being open defers the removal; it no longer fails it.</b> On a busy
/// laptop Chrome is open all day, so the old refusal meant an operator retrying
/// until they hit the right moment. Now, when the engine reports that Chrome
/// running for the user is the ONLY thing in the way -- every stale/wrong-request
/// gate already passed -- the request is handed to
/// <see cref="ChromeProfileRemovalWorker"/> (persisted, surviving restarts and
/// reboots, deduplicated per user+profile) and the task completes as SUCCEEDED
/// with a message saying so and <c>{profileKey, scheduled: true}</c> in the
/// result, so the console shows one click, not a failure to retry.
/// </para>
/// <para>
/// <b>Every other refusal stays a refusal.</b> A bad payload, the Default
/// profile, an unresolvable account, a path or account that does not match this
/// device -- a stale or wrong request must never sit scheduled, so these fail
/// immediately, exactly as before, whether or not Chrome happens to be running.
/// </para>
/// </remarks>
public sealed class RemoveChromeProfileExecutor : ITaskExecutor
{
    /// <summary>
    /// The exact completion message a deferred removal reports. "The moment" is
    /// honest to within the worker's two-minute check; the console's Chrome list
    /// only changes after the next inventory refresh in any case.
    /// </summary>
    internal const string ScheduledMessage =
        "Scheduled: Chrome is open for this user; the profile will be removed the moment "
        + "Chrome closes or at the next startup. The console reflects it after the next "
        + "Chrome inventory refresh.";

    private readonly ChromeProfileRemovalEngine _engine;
    private readonly ChromeProfileRemovalWorker _worker;
    private readonly ILogger<RemoveChromeProfileExecutor> _logger;
    private readonly TimeProvider _time;

    public RemoveChromeProfileExecutor(
        ChromeProfileRemovalEngine engine,
        ChromeProfileRemovalWorker worker,
        ILogger<RemoveChromeProfileExecutor> logger,
        TimeProvider? timeProvider = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = timeProvider ?? TimeProvider.System;
    }

    public string TaskType => "RemoveChromeProfile";

    public async Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (!TryParse(task.PayloadJson, out var payload, out var refusal))
        {
            _logger.LogWarning("Chrome profile removal task {TaskId} refused: {Reason}", task.TaskId, refusal);
            return new AgentTaskResult(false, refusal, null);
        }

        var outcome = await _engine.RunAsync(task.TaskId, payload, cancellationToken);

        if (outcome.Disposition == ChromeProfileRemovalDisposition.ChromeRunning)
        {
            // The one deferrable blocker, and it clears itself. Park the request
            // (the worker logs the queueing under this task's id) and complete
            // the task: the operator has done their part with one click.
            await _worker.ScheduleAsync(
                new PendingChromeProfileRemoval(
                    task.TaskId, payload.UserSid, payload.ProfileKey, payload.ExpectedEmail,
                    payload.ProfilePath, _time.GetUtcNow()),
                cancellationToken);

            return new AgentTaskResult(true, ScheduledMessage,
                JsonSerializer.Serialize(new { profileKey = payload.ProfileKey, scheduled = true }));
        }

        // The engine already logged the outcome under this task's id.
        return new AgentTaskResult(
            outcome.Disposition == ChromeProfileRemovalDisposition.Removed,
            outcome.Message,
            outcome.ResultJson);
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

            if (string.Equals(profileKey, ChromeProfileRemovalEngine.DefaultProfileKey, StringComparison.OrdinalIgnoreCase))
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
}
