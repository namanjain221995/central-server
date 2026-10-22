using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.ChromePolicy;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.Tasks;

/// <summary>
/// Applies the complete Chrome managed-extension policy the server has issued for
/// this machine, for an <c>ApplyChromeExtensionPolicy</c> task.
/// </summary>
/// <remarks>
/// <para>
/// The payload is whole state, so this executor never merges: the rules it
/// carries are every rule for this device, an extension absent from them is
/// unmanaged, and an empty rule list means "no managed extension policy" and
/// clears what the agent wrote. That also makes the task safe to run however long
/// it sat queued -- applying the same state twice is a no-op, and there is no
/// expiry refusal here the way a restart has one, because the policy does not go
/// stale with time, only with a newer policy.
/// </para>
/// <para>
/// A newer policy is exactly what the <c>issuedAt</c> guard protects
/// (the ApplyUsbPolicy precedent): a task issued before the policy most recently
/// applied is ignored rather than applied, so late delivery of an old task cannot
/// roll the device back to a policy the server has since replaced. Ignoring a
/// stale policy is a success -- the state the server wants is already in force --
/// reported with <c>applied: 0</c> so the console is never told rules took hold
/// that did not.
/// </para>
/// <para>
/// Every parse failure is a refusal that writes nothing: a malformed payload, an
/// invalid or duplicated extension id, or a mode outside the contract's four
/// names. An unknown mode is never approximated to a known one -- a payload
/// cannot talk this agent into an enforcement it does not implement -- while
/// unknown <em>extra properties</em> are tolerated and ignored, so a newer server
/// adding a field does not strand older agents. The write itself is honest too: a
/// writer that could not reach the registry fails the task rather than reporting
/// a policy in place that is not.
/// </para>
/// </remarks>
public sealed class ApplyChromeExtensionPolicyExecutor(
    IChromeExtensionPolicyWriter writer,
    ILogger<ApplyChromeExtensionPolicyExecutor> logger) : ITaskExecutor
{
    /// <summary>Refuses absurd rule counts before parsing them. Far above any real managed fleet's list.</summary>
    public const int MaxRules = 1000;

    private readonly IChromeExtensionPolicyWriter _writer = writer
        ?? throw new ArgumentNullException(nameof(writer));

    private readonly ILogger<ApplyChromeExtensionPolicyExecutor> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    private readonly object _gate = new();

    /// <summary>
    /// The <c>issuedAt</c> of the newest policy this process successfully applied
    /// or cleared. In-memory on purpose: the registry already holds the newest
    /// state across restarts, tasks arrive and run in queue order, and the guard
    /// exists for the one hazard left -- a stale task re-delivered after a newer
    /// one has run. A failed write never records, so a retry of the same policy
    /// is not mistaken for already-applied.
    /// </summary>
    private DateTimeOffset? _newestApplied;

    public string TaskType => "ApplyChromeExtensionPolicy";

    public Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (!TryParse(task.PayloadJson, out var rules, out var issuedAt, out var refusal))
        {
            _logger.LogWarning("Chrome extension policy task {TaskId} refused: {Reason}", task.TaskId, refusal);
            return Task.FromResult(new AgentTaskResult(false, refusal, null));
        }

        // Validity before staleness: a payload that should never be enforced is
        // refused as such, not waved through as "superseded".
        string? settingsJson = null;
        if (rules.Count > 0
            && !ChromeExtensionPolicyDoc.TryBuild(rules, out settingsJson, out var buildRefusal))
        {
            _logger.LogWarning("Chrome extension policy task {TaskId} refused: {Reason}", task.TaskId, buildRefusal);
            return Task.FromResult(new AgentTaskResult(false, buildRefusal, null));
        }

        lock (_gate)
        {
            if (_newestApplied is { } newest && issuedAt < newest)
            {
                _logger.LogInformation(
                    "Ignoring a Chrome extension policy issued {IssuedAt} because a newer one issued {Current} "
                    + "is already in force. A late policy must not roll the device back.",
                    issuedAt, newest);

                return Task.FromResult(new AgentTaskResult(
                    true,
                    $"Chrome extension policy not applied: a newer policy issued {newest:u} is already in force.",
                    BuildResultJson(applied: 0, cleared: false)));
            }
        }

        if (rules.Count == 0)
        {
            if (!_writer.Clear())
            {
                return Task.FromResult(new AgentTaskResult(
                    false,
                    "The Chrome extension policy could not be cleared from the machine policy registry key.",
                    null));
            }

            RecordApplied(issuedAt);
            _logger.LogInformation("Chrome extension policy cleared (issued {IssuedAt}).", issuedAt);
            return Task.FromResult(new AgentTaskResult(
                true, "Chrome extension policy cleared.", BuildResultJson(applied: 0, cleared: true)));
        }

        if (!_writer.Apply(settingsJson!))
        {
            return Task.FromResult(new AgentTaskResult(
                false,
                "The Chrome extension policy could not be written to the machine policy registry key.",
                null));
        }

        RecordApplied(issuedAt);
        var message = $"Chrome extension policy applied: {rules.Count} rule(s).";
        _logger.LogInformation("{Message} (issued {IssuedAt})", message, issuedAt);
        return Task.FromResult(new AgentTaskResult(
            true, message, BuildResultJson(applied: rules.Count, cleared: false)));
    }

    /// <summary>
    /// Records a successful apply or clear, keeping the newest timestamp. Equal
    /// timestamps re-record so a replay of the current policy stays a success.
    /// </summary>
    private void RecordApplied(DateTimeOffset issuedAt)
    {
        lock (_gate)
        {
            if (_newestApplied is not { } newest || issuedAt >= newest)
            {
                _newestApplied = issuedAt;
            }
        }
    }

    /// <summary>
    /// The structured result the server parses: how many rules the written
    /// document carries, and whether the managed policy was cleared instead.
    /// </summary>
    internal static string BuildResultJson(int applied, bool cleared) =>
        JsonSerializer.Serialize(new { applied, cleared });

    /// <summary>
    /// Reads the whole-state payload. Strict: <c>rules</c> must be an array of
    /// objects whose <c>extensionId</c> is shaped like a Chrome extension id and
    /// whose <c>mode</c> is one of the contract's four names, and <c>issuedAt</c>
    /// must be a parseable timestamp (it drives the stale-policy guard, so a
    /// payload without one cannot be ordered and is refused). Unknown extra
    /// properties anywhere are tolerated and ignored -- the payload arrives over
    /// the authenticated task channel. The id and duplicate checks run again in
    /// <see cref="ChromeExtensionPolicyDoc.TryBuild"/>; belt and braces, so no
    /// path to the registry can skip them.
    /// </summary>
    internal static bool TryParse(
        string? payloadJson,
        out List<ChromeExtensionPolicyRule> rules,
        out DateTimeOffset issuedAt,
        [NotNullWhen(false)] out string? refusal)
    {
        rules = [];
        issuedAt = default;

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            refusal = "The Chrome extension policy task carried no payload.";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payloadJson);
        }
        catch (JsonException)
        {
            refusal = "The Chrome extension policy payload was not valid JSON.";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                refusal = "The Chrome extension policy payload was not a JSON object.";
                return false;
            }

            if (!root.TryGetProperty("issuedAt", out var issuedAtElement)
                || issuedAtElement.ValueKind != JsonValueKind.String
                || !issuedAtElement.TryGetDateTimeOffset(out issuedAt))
            {
                refusal = "The Chrome extension policy payload carried no usable 'issuedAt' timestamp.";
                return false;
            }

            if (!root.TryGetProperty("rules", out var rulesElement)
                || rulesElement.ValueKind != JsonValueKind.Array)
            {
                refusal = "The Chrome extension policy payload carried no 'rules' array.";
                return false;
            }

            if (rulesElement.GetArrayLength() > MaxRules)
            {
                refusal = $"Refused a Chrome extension policy naming more than {MaxRules} extensions.";
                return false;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in rulesElement.EnumerateArray())
            {
                // Every field is checked for its JSON kind before it is read, so
                // one wrong-typed field produces the precise refusal below rather
                // than an exception blurring into "malformed payload".
                if (element.ValueKind != JsonValueKind.Object)
                {
                    refusal = "A Chrome extension policy rule was not a JSON object.";
                    return false;
                }

                if (!element.TryGetProperty("extensionId", out var idElement)
                    || idElement.ValueKind != JsonValueKind.String
                    || idElement.GetString() is not { } extensionId
                    || !InventoryChromeExtension.IsValidExtensionId(extensionId))
                {
                    // A refusal, not a skip: dropping the bad rule and enforcing
                    // the rest would be a policy the server never issued.
                    refusal = "A Chrome extension policy rule named an invalid extension id; "
                        + "an id is exactly 32 characters, each a-p.";
                    return false;
                }

                if (!element.TryGetProperty("mode", out var modeElement)
                    || modeElement.ValueKind != JsonValueKind.String
                    || !TryParseMode(modeElement.GetString(), out var mode))
                {
                    // Exactly four accepted names, as strings. Anything else -- an
                    // unknown name, or the enum-as-number serialisation that has
                    // bitten this codebase before -- is refused rather than
                    // guessed, so a payload cannot reach an enforcement it never
                    // named (and above all cannot force-install by accident).
                    refusal = "A Chrome extension policy rule carried an unknown mode; expected "
                        + "'ForceInstalled', 'Blocked', 'Allowed' or 'Removed'.";
                    return false;
                }

                if (!seen.Add(extensionId))
                {
                    refusal = "The Chrome extension policy named the same extension more than once.";
                    return false;
                }

                rules.Add(new ChromeExtensionPolicyRule(extensionId, mode));
            }

            refusal = null;
            return true;
        }
    }

    /// <summary>
    /// Maps a wire mode name onto a mode this agent enforces. Written out rather
    /// than delegated to <c>Enum.TryParse</c>, which would accept a bare ordinal
    /// ("0") as <see cref="ChromeExtensionPolicyMode.ForceInstalled"/> -- the
    /// widest mode reached without ever naming it.
    /// </summary>
    private static bool TryParseMode(string? value, out ChromeExtensionPolicyMode mode)
    {
        if (string.Equals(value, nameof(ChromeExtensionPolicyMode.ForceInstalled), StringComparison.OrdinalIgnoreCase))
        {
            mode = ChromeExtensionPolicyMode.ForceInstalled;
            return true;
        }

        if (string.Equals(value, nameof(ChromeExtensionPolicyMode.Blocked), StringComparison.OrdinalIgnoreCase))
        {
            mode = ChromeExtensionPolicyMode.Blocked;
            return true;
        }

        if (string.Equals(value, nameof(ChromeExtensionPolicyMode.Allowed), StringComparison.OrdinalIgnoreCase))
        {
            mode = ChromeExtensionPolicyMode.Allowed;
            return true;
        }

        if (string.Equals(value, nameof(ChromeExtensionPolicyMode.Removed), StringComparison.OrdinalIgnoreCase))
        {
            mode = ChromeExtensionPolicyMode.Removed;
            return true;
        }

        mode = default;
        return false;
    }
}
