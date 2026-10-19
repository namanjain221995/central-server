using System.Text.Json;
using EndpointAgent.Core.ScreenLock;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Tasks;

/// <summary>
/// Engages a persistent full-screen lock on the device: the server's message and
/// contact line on an overlay in every session, the machine locked down, and the
/// whole thing released only by a <c>ReleaseScreenLock</c> task or a correct
/// on-device unlock code.
/// </summary>
/// <remarks>
/// <para>
/// <b>The payload is validated before anything is shown or changed.</b> The
/// message and contact line are held to the plain-text bounds the overlay and the
/// pipe enforce (<see cref="ScreenLockText"/>), and the unlock verifier must be a
/// verifier this build can actually match against -- the known algorithm, a sane
/// iteration count, and salt and hash that decode. A payload that is missing,
/// malformed or out of bounds is a failure result, and nothing is engaged: a lock
/// that could never be released by a correct code must never be put up.
/// </para>
/// <para>
/// <b>An expired task engages nothing.</b> A lock is a heavy, machine-wide action;
/// a task that sat past its deadline (a long stall, a badly skewed clock) is
/// refused rather than applied late, exactly as a late restart is.
/// </para>
/// </remarks>
public sealed class EngageScreenLockExecutor(
    ScreenLockManager manager,
    ILogger<EngageScreenLockExecutor> logger,
    TimeProvider? timeProvider = null) : ITaskExecutor
{
    private readonly ScreenLockManager _manager = manager;
    private readonly ILogger<EngageScreenLockExecutor> _logger = logger;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public string TaskType => "EngageScreenLock";

    public async Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        var now = _time.GetUtcNow();

        if (task.ExpiresAt is { } deadline && now >= deadline)
        {
            _logger.LogWarning(
                "Screen-lock task {TaskId} refused: its deadline {Deadline:u} passed before execution (now {Now:u}).",
                task.TaskId, deadline, now);
            return new AgentTaskResult(
                false,
                $"Screen lock refused: the task expired at {deadline:u} before the device executed it; nothing was locked.",
                null);
        }

        if (!TryParse(task.PayloadJson, out var lockId, out var message, out var contactLine, out var verifier, out var refusal))
        {
            _logger.LogWarning("Screen-lock task {TaskId} refused: {Reason}", task.TaskId, refusal);
            return new AgentTaskResult(false, $"Screen lock refused: {refusal} Nothing was locked.", null);
        }

        var state = new ScreenLockState(lockId, message, contactLine, now, verifier);

        try
        {
            await _manager.EngageAsync(state, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        _logger.LogWarning("Screen-lock task {TaskId}: lock {LockId} engaged.", task.TaskId, lockId);
        return new AgentTaskResult(true, "Screen lock engaged.", ResultJson(lockId));
    }

    /// <summary>
    /// Reads the payload the server queued: the lock id, the words to show, and the
    /// PBKDF2 verifier the device releases against. Unknown extra properties are
    /// tolerated -- the payload arrives over the authenticated task channel -- but
    /// everything that matters must be exactly right, and the verifier must be one
    /// a correct code could match.
    /// </summary>
    private static bool TryParse(
        string? payloadJson,
        out Guid lockId,
        out string message,
        out string? contactLine,
        out ScreenLockVerifier verifier,
        out string refusal)
    {
        lockId = Guid.Empty;
        message = string.Empty;
        contactLine = null;
        verifier = new ScreenLockVerifier(string.Empty, 0, string.Empty, string.Empty);

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            refusal = "the task carried no payload.";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payloadJson);
        }
        catch (JsonException)
        {
            refusal = "the payload was not valid JSON.";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                refusal = "the payload was not a JSON object.";
                return false;
            }

            if (!root.TryGetProperty("lockId", out var idElement) || !idElement.TryGetGuid(out lockId) || lockId == Guid.Empty)
            {
                refusal = "the payload named no lock id.";
                return false;
            }

            if (!root.TryGetProperty("message", out var messageElement)
                || messageElement.ValueKind != JsonValueKind.String
                || !ScreenLockText.IsValidMessage(messageElement.GetString()))
            {
                refusal = $"the message must be plain text of 1 to {ScreenLockText.MaxMessageChars} characters.";
                return false;
            }

            message = messageElement.GetString()!;

            if (root.TryGetProperty("contactLine", out var contactElement) && contactElement.ValueKind != JsonValueKind.Null)
            {
                if (contactElement.ValueKind != JsonValueKind.String || !ScreenLockText.IsValidContact(contactElement.GetString()))
                {
                    refusal = $"the contact line, when given, must be plain text of up to {ScreenLockText.MaxContactChars} characters.";
                    return false;
                }

                contactLine = contactElement.GetString();
            }

            if (!TryParseVerifier(root, out verifier, out refusal))
            {
                return false;
            }

            refusal = string.Empty;
            return true;
        }
    }

    private static bool TryParseVerifier(JsonElement root, out ScreenLockVerifier verifier, out string refusal)
    {
        verifier = new ScreenLockVerifier(string.Empty, 0, string.Empty, string.Empty);

        if (!root.TryGetProperty("unlock", out var unlock) || unlock.ValueKind != JsonValueKind.Object)
        {
            refusal = "the payload carried no unlock verifier.";
            return false;
        }

        if (!unlock.TryGetProperty("algo", out var algo) || algo.ValueKind != JsonValueKind.String
            || !unlock.TryGetProperty("iterations", out var iterations) || iterations.ValueKind != JsonValueKind.Number
            || !iterations.TryGetInt32(out var iterationCount)
            || !unlock.TryGetProperty("salt", out var salt) || salt.ValueKind != JsonValueKind.String
            || !unlock.TryGetProperty("hash", out var hash) || hash.ValueKind != JsonValueKind.String)
        {
            refusal = "the unlock verifier must have algo, iterations, salt and hash.";
            return false;
        }

        var candidate = new ScreenLockVerifier(algo.GetString()!, iterationCount, salt.GetString()!, hash.GetString()!);
        if (!candidate.IsWellFormed())
        {
            refusal = $"the unlock verifier must be {ScreenLockVerifier.Pbkdf2Sha256} with a sane iteration count and decodable salt and hash.";
            return false;
        }

        verifier = candidate;
        refusal = string.Empty;
        return true;
    }

    private static string ResultJson(Guid lockId) => JsonSerializer.Serialize(new { outcome = "Engaged", lockId });
}
