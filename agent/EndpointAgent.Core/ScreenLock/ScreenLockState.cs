using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EndpointAgent.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointAgent.Core.ScreenLock;

/// <summary>
/// The PBKDF2 verifier an engaged screen lock is released against: the recipe the
/// server used to derive its hash, so the device can repeat it against a code a
/// user types and compare the results.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only the SYSTEM service ever sees this.</b> It is persisted in the
/// ACL-protected state directory and never travels to a user session: the overlay
/// sends a candidate code over the pipe and the service does the verification. So
/// a user who controls the overlay learns nothing that would let them derive the
/// code offline.
/// </para>
/// <para>
/// A verifier, not the code itself: the device can confirm a correct code but can
/// never recover it, exactly as a password hash does. The derived length matches
/// the stored hash, so the server chooses it; the comparison is constant-time, so
/// a wrong guess reveals nothing by how long it took.
/// </para>
/// </remarks>
/// <param name="Algo">The derivation, which must be <see cref="Pbkdf2Sha256"/>; any other is refused.</param>
/// <param name="Iterations">PBKDF2 iteration count, as the server used it.</param>
/// <param name="Salt">Base64 salt, as the server used it.</param>
/// <param name="Hash">Base64 of the server's derived key; the device derives the same length and compares.</param>
public sealed record ScreenLockVerifier(string Algo, int Iterations, string Salt, string Hash)
{
    /// <summary>The one derivation this build understands.</summary>
    public const string Pbkdf2Sha256 = "pbkdf2-sha256";

    /// <summary>
    /// Whether this verifier's fields are a verifier this build can use at all:
    /// the known algorithm, a sane iteration count, and salt and hash that decode
    /// to plausible lengths. Checked by the executor before anything is engaged,
    /// so a lock is never persisted that could never be released by a correct code.
    /// </summary>
    public bool IsWellFormed()
    {
        if (!string.Equals(Algo, Pbkdf2Sha256, StringComparison.Ordinal)
            || Iterations is < MinIterations or > MaxIterations)
        {
            return false;
        }

        return TryDecode(Salt, out var salt) && salt.Length >= MinSaltBytes
            && TryDecode(Hash, out var hash) && hash.Length is >= MinHashBytes and <= MaxHashBytes;
    }

    /// <summary>
    /// Whether <paramref name="code"/> is the recovery code behind this verifier.
    /// Constant-time, so a near-miss cannot be distinguished from a wild guess by
    /// timing. A malformed verifier (which <see cref="IsWellFormed"/> would have
    /// refused at engage time) verifies nothing.
    /// </summary>
    public bool Verify(string? code)
    {
        if (string.IsNullOrEmpty(code) || !string.Equals(Algo, Pbkdf2Sha256, StringComparison.Ordinal))
        {
            return false;
        }

        if (Iterations <= 0 || !TryDecode(Salt, out var salt) || !TryDecode(Hash, out var expected)
            || salt.Length == 0 || expected.Length == 0)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(code), salt, Iterations, HashAlgorithmName.SHA256, expected.Length);

        // Constant-time: the comparison takes the same shape for a correct code
        // and an almost-correct one, so repeated guesses learn nothing from timing.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    // Bounds for what the executor will accept. Low enough not to reject a real
    // server verifier, high enough that an absurd iteration count cannot be used
    // to make a single verification pin a core for minutes.
    private const int MinIterations = 10_000;
    private const int MaxIterations = 10_000_000;
    private const int MinSaltBytes = 8;
    private const int MinHashBytes = 16;
    private const int MaxHashBytes = 64;

    private static bool TryDecode(string value, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}

/// <summary>
/// A screen lock this device is holding: the words shown on the overlay, when it
/// was engaged, the verifier it is released against, and the machine settings that
/// were changed to lock it down -- captured so release can put them back exactly.
/// </summary>
/// <remarks>
/// Persisted the moment a lock is engaged so it survives a service restart and a
/// reboot: a locked device that reboots comes back locked, which is the whole
/// point of a persistent lock. A torn or missing file reads as "not locked"
/// (<see cref="FileScreenLockStore"/>), the recovery-safe misreading -- a corrupt
/// file frees a user rather than stranding them behind a verifier that can no
/// longer be matched.
/// </remarks>
/// <param name="LockId">The server's EngageScreenLock task; a release names it.</param>
/// <param name="Message">The plain-text message the overlay shows. Never parsed, never a command.</param>
/// <param name="ContactLine">An optional plain-text contact line, or null.</param>
/// <param name="EngagedAt">When the device engaged it, by this machine's clock.</param>
/// <param name="Verifier">What a release code is checked against; seen only by the SYSTEM service.</param>
/// <param name="PriorPolicy">
/// An opaque snapshot of the machine settings the lockdown changed, as the Windows
/// lockdown applier wrote it. The service persists it and hands it back to
/// <c>IScreenLockdown.Restore</c> on release; Core never interprets it.
/// </param>
public sealed record ScreenLockState(
    Guid LockId,
    string Message,
    string? ContactLine,
    DateTimeOffset EngagedAt,
    ScreenLockVerifier Verifier,
    string? PriorPolicy = null);

/// <summary>
/// Where the one engaged screen lock survives a service restart or a reboot.
/// Nothing about the lock may depend on the network, so it may not depend on
/// memory either.
/// </summary>
public interface IScreenLockStore
{
    /// <summary>The engaged lock, or null when none is -- including when the store is missing or unreadable.</summary>
    ValueTask<ScreenLockState?> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists the lock, or clears it when <paramref name="state"/> is null.</summary>
    ValueTask SaveAsync(ScreenLockState? state, CancellationToken cancellationToken = default);
}

/// <summary>
/// A JSON file in the agent's state directory, which the installer restricts to
/// SYSTEM and Administrators -- an ordinary user can neither read the verifier nor
/// edit the lock on the machine they are locked out of.
/// </summary>
public sealed class FileScreenLockStore : IScreenLockStore
{
    public const string StateFileName = "screen-lock.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _stateDirectory;
    private readonly ILogger<FileScreenLockStore> _logger;

    public FileScreenLockStore(IOptions<AgentOptions> options, ILogger<FileScreenLockStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _stateDirectory = options.Value.StateDirectory ?? AgentPaths.StateDirectory;
    }

    private string StatePath => Path.Combine(_stateDirectory, StateFileName);

    public async ValueTask<ScreenLockState?> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<ScreenLockState>(
                await File.ReadAllBytesAsync(StatePath, cancellationToken), Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Null means "not locked", which is the recovery-safe misreading: a
            // damaged lock file frees a user, it never strands one behind a
            // verifier that can no longer be matched.
            _logger.LogError(ex, "Could not read {Path}; the device is treated as not locked.", StatePath);
            return null;
        }
    }

    public async ValueTask SaveAsync(ScreenLockState? state, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_stateDirectory);

        if (state is null)
        {
            // Release clears the lock by removing the file. Restore has already run
            // and put the machine back, so a missing file means "not locked".
            if (File.Exists(StatePath))
            {
                File.Delete(StatePath);
            }

            return;
        }

        // Write-then-move: a torn file reads as "not locked", never as half a lock.
        var temporaryPath = StatePath + ".tmp";
        await File.WriteAllBytesAsync(temporaryPath, JsonSerializer.SerializeToUtf8Bytes(state, Json), cancellationToken);
        File.Move(temporaryPath, StatePath, overwrite: true);
    }
}

/// <summary>
/// The bounds and the one plain-text rule the screen lock's words share with the
/// wire protocol, so a message the executor accepts is one the overlay can show
/// and the pipe can carry, and the reverse can never surprise anyone.
/// </summary>
public static class ScreenLockText
{
    public const int MaxMessageChars = 500;

    public const int MaxContactChars = 200;

    /// <summary>A message must be present, within length, and plain text.</summary>
    public static bool IsValidMessage(string? message) => IsPlainText(message, MaxMessageChars);

    /// <summary>A contact line is optional; when present it must be within length and plain text.</summary>
    public static bool IsValidContact(string? contact) => contact is null || IsPlainText(contact, MaxContactChars);

    private static bool IsPlainText(string? text, int maxChars) =>
        !string.IsNullOrEmpty(text)
        && text.Length <= maxChars
        && !text.Any(static c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'));
}
