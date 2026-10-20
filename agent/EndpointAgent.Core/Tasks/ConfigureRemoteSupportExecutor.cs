using System.Text.Json;
using EndpointAgent.Core.Abstractions;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Tasks;

/// <summary>
/// Configures the device's already-installed RustDesk for IT remote support by
/// setting a platform-managed unattended password.
/// </summary>
/// <remarks>
/// <para>
/// <b>The password never rides in the payload.</b> The payload carries only a
/// one-time reference, redeemed here over the authenticated channel immediately
/// before use, exactly as the local-account executors redeem a password. A reference
/// that cannot be redeemed (expired, replayed, or another device's) fails the task
/// and nothing is applied -- the operator re-issues with a fresh secret.
/// </para>
/// <para>
/// <b>An expired task configures nothing.</b> Setting an unattended password grants
/// live control of the machine; a task that sat past its deadline (a long stall, a
/// badly skewed clock) is refused rather than applied late, exactly as a late screen
/// lock or restart is.
/// </para>
/// <para>
/// <b>No result payload.</b> The thing the console needs -- the device's RustDesk ID
/// -- is reported on the heartbeat, not here, so <c>ResultJson</c> is null.
/// </para>
/// </remarks>
public sealed class ConfigureRemoteSupportExecutor(
    IRemoteSupportControl control,
    ISecretRedeemer secrets,
    ILogger<ConfigureRemoteSupportExecutor> logger,
    TimeProvider? timeProvider = null) : ITaskExecutor
{
    private readonly IRemoteSupportControl _control = control ?? throw new ArgumentNullException(nameof(control));
    private readonly ISecretRedeemer _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
    private readonly ILogger<ConfigureRemoteSupportExecutor> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public string TaskType => "ConfigureRemoteSupport";

    public async Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        var now = _time.GetUtcNow();

        if (task.ExpiresAt is { } deadline && now >= deadline)
        {
            _logger.LogWarning(
                "Remote-support task {TaskId} refused: its deadline {Deadline:u} passed before execution (now {Now:u}).",
                task.TaskId, deadline, now);
            return new AgentTaskResult(
                false,
                $"Remote support configuration refused: the task expired at {deadline:u} before the device executed it; nothing was changed.",
                null);
        }

        if (!TryParse(task.PayloadJson, out var secretRef, out var refusal))
        {
            _logger.LogWarning("Remote-support task {TaskId} refused: {Reason}", task.TaskId, refusal);
            return new AgentTaskResult(false, $"Remote support configuration refused: {refusal}", null);
        }

        var password = await _secrets.RedeemAsync(secretRef, cancellationToken);
        if (password is null)
        {
            // One-time reference: expired, already used, or not ours. Never retried
            // blindly - the operator re-issues with a fresh secret.
            return new AgentTaskResult(
                false, "The password reference could not be redeemed; the task was not applied.", null);
        }

        RemoteSupportConfigureResult outcome;
        try
        {
            outcome = await _control.SetUnattendedPasswordAsync(password, cancellationToken);
        }
        finally
        {
            // Drop the plaintext the instant the control layer is done with it.
            password = null;
        }

        if (outcome.Succeeded)
        {
            _logger.LogInformation("Remote-support task {TaskId}: unattended access configured.", task.TaskId);
        }
        else
        {
            _logger.LogWarning(
                "Remote-support task {TaskId}: configuration did not succeed: {Message}", task.TaskId, outcome.Message);
        }

        // ResultJson is null by design: the device's RustDesk ID is reported on the
        // heartbeat, so there is nothing to carry back here.
        return new AgentTaskResult(
            outcome.Succeeded,
            outcome.Message ?? (outcome.Succeeded ? "Remote support access configured." : "Remote support configuration failed."),
            null);
    }

    /// <summary>
    /// Reads the one thing the payload carries: the one-time reference the password
    /// is redeemed against. Strict -- a missing, malformed or empty reference is a
    /// refusal, so nothing is redeemed or applied on a bad payload. Unknown extra
    /// properties are tolerated, as the payload arrives over the authenticated task
    /// channel.
    /// </summary>
    private static bool TryParse(string? payloadJson, out string secretRef, out string refusal)
    {
        secretRef = string.Empty;

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

            if (!root.TryGetProperty("passwordSecretRef", out var refElement)
                || refElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(refElement.GetString()))
            {
                refusal = "the payload named no password reference.";
                return false;
            }

            secretRef = refElement.GetString()!;
            refusal = string.Empty;
            return true;
        }
    }
}
