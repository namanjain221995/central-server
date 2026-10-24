using System.Text.Json;
using EndpointAgent.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointAgent.Core.ChromeProfiles;

/// <summary>
/// A Chrome profile removal the operator asked for while Chrome was open for
/// that user: the device holds the intent and carries it out the moment Chrome
/// is not in the way -- after Chrome closes, or at the next service start.
/// </summary>
/// <param name="TaskId">The original RemoveChromeProfile task, so the deferred completion's log lines read back to the click that asked for it.</param>
/// <param name="UserSid">The Windows account whose Chrome holds the profile.</param>
/// <param name="ProfileKey">The profile directory name under User Data, e.g. "Profile 9".</param>
/// <param name="ExpectedEmail">The account the console saw signed into the profile, or null when it saw none; re-checked on every attempt.</param>
/// <param name="ProfilePath">The full profile path the console computed; re-checked against this device on every attempt.</param>
/// <param name="RequestedAt">When the operator (most recently) asked, by this machine's clock; a request too old is dropped, not fired.</param>
public sealed record PendingChromeProfileRemoval(
    Guid TaskId,
    string UserSid,
    string ProfileKey,
    string? ExpectedEmail,
    string ProfilePath,
    DateTimeOffset RequestedAt)
{
    /// <summary>The request as the removal engine takes it -- the same shape the live task runs with.</summary>
    public RemoveChromeProfilePayload ToPayload() => new(UserSid, ProfileKey, ExpectedEmail, ProfilePath);

    /// <summary>
    /// Whether <paramref name="other"/> names the same profile of the same user:
    /// the deduplication identity. Case-insensitive on both, because SIDs compare
    /// caseless and a profile key names an NTFS directory.
    /// </summary>
    public bool SameTarget(PendingChromeProfileRemoval other) =>
        string.Equals(UserSid, other.UserSid, StringComparison.OrdinalIgnoreCase)
        && string.Equals(ProfileKey, other.ProfileKey, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Where deferred Chrome profile removals survive a service restart or a reboot.
/// The whole point of deferring is that the intent outlives the moment Chrome
/// was open, so it may not live in memory.
/// </summary>
public interface IPendingChromeRemovalStore
{
    /// <summary>Everything pending. Empty rather than throwing when nothing is stored or the store is unreadable.</summary>
    ValueTask<IReadOnlyList<PendingChromeProfileRemoval>> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(IReadOnlyList<PendingChromeProfileRemoval> removals, CancellationToken cancellationToken = default);
}

/// <summary>
/// A JSON file in the agent's state directory, which the installer restricts to
/// SYSTEM and Administrators.
/// </summary>
/// <remarks>
/// Plain JSON, deliberately NOT DPAPI-sealed (unlike
/// <c>DpapiEnrollmentStateStore</c>, whose file redeems an enrollment): nothing
/// in here is a secret -- a SID, a profile directory name, a profile path, the
/// account address the console already displays, a timestamp and a task id --
/// and the directory ACL already keeps ordinary users out. Sealing it would only
/// make a stuck removal harder to diagnose.
/// </remarks>
public sealed class FilePendingChromeRemovalStore : IPendingChromeRemovalStore
{
    public const string StateFileName = "pending-chrome-removals.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _stateDirectory;
    private readonly ILogger<FilePendingChromeRemovalStore> _logger;

    public FilePendingChromeRemovalStore(IOptions<AgentOptions> options, ILogger<FilePendingChromeRemovalStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _stateDirectory = options.Value.StateDirectory ?? AgentPaths.StateDirectory;
    }

    private string StatePath => Path.Combine(_stateDirectory, StateFileName);

    public async ValueTask<IReadOnlyList<PendingChromeProfileRemoval>> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return [];
            }

            return JsonSerializer.Deserialize<PendingChromeProfileRemoval[]>(
                await File.ReadAllBytesAsync(StatePath, cancellationToken), Json) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Empty is the safe misreading: a damaged file costs the operator one
            // more click in the console, never deletes something unasked.
            _logger.LogError(ex, "Could not read {Path}; deferred Chrome profile removals on this device are forgotten.", StatePath);
            return [];
        }
    }

    public async ValueTask SaveAsync(IReadOnlyList<PendingChromeProfileRemoval> removals, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(removals);
        Directory.CreateDirectory(_stateDirectory);

        // Write-then-move: a torn file reads as "nothing pending", never as half an entry.
        var temporaryPath = StatePath + ".tmp";
        await File.WriteAllBytesAsync(temporaryPath, JsonSerializer.SerializeToUtf8Bytes(removals, Json), cancellationToken);
        File.Move(temporaryPath, StatePath, overwrite: true);
    }
}
