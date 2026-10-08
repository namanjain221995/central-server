using System.Text.Json;
using EndpointAgent.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointAgent.Core.Restarts;

/// <summary>
/// A restart the server scheduled and this device has taken charge of: it
/// happens at <see cref="DueAt"/> whether or not the device can reach the server
/// by then.
/// </summary>
/// <param name="TaskId">The server's ScheduleRestart task; a cancellation names it.</param>
/// <param name="DueAt">When to restart, by THIS machine's clock (already corrected for skew).</param>
/// <param name="WarningSeconds">How long Windows counts down, showing its warning, before the restart.</param>
/// <param name="Message">The words Windows shows during the countdown.</param>
/// <param name="ArmedAt">When the device accepted it, by this machine's clock.</param>
public sealed record ArmedRestart(Guid TaskId, DateTimeOffset DueAt, int WarningSeconds, string? Message, DateTimeOffset ArmedAt);

/// <summary>
/// Where armed restarts survive a service restart or a reboot before they are
/// due. Nothing about a restart may depend on the network once it is armed, so
/// it may not depend on memory either.
/// </summary>
public interface IArmedRestartStore
{
    /// <summary>Everything armed. Empty rather than throwing when nothing is stored or the store is unreadable.</summary>
    ValueTask<IReadOnlyList<ArmedRestart>> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(IReadOnlyList<ArmedRestart> restarts, CancellationToken cancellationToken = default);
}

/// <summary>
/// A JSON file in the agent's state directory, which the installer restricts to
/// SYSTEM and Administrators -- an ordinary user can neither read nor edit when
/// their machine is going to restart.
/// </summary>
public sealed class FileArmedRestartStore : IArmedRestartStore
{
    public const string StateFileName = "armed-restarts.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _stateDirectory;
    private readonly ILogger<FileArmedRestartStore> _logger;

    public FileArmedRestartStore(IOptions<AgentOptions> options, ILogger<FileArmedRestartStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _stateDirectory = options.Value.StateDirectory ?? AgentPaths.StateDirectory;
    }

    private string StatePath => Path.Combine(_stateDirectory, StateFileName);

    public async ValueTask<IReadOnlyList<ArmedRestart>> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return [];
            }

            return JsonSerializer.Deserialize<ArmedRestart[]>(
                await File.ReadAllBytesAsync(StatePath, cancellationToken), Json) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Empty means "no restart is coming", which is the safe misreading:
            // a damaged file costs a restart, never causes an unexpected one.
            _logger.LogError(ex, "Could not read {Path}; scheduled restarts on this device are forgotten.", StatePath);
            return [];
        }
    }

    public async ValueTask SaveAsync(IReadOnlyList<ArmedRestart> restarts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(restarts);
        Directory.CreateDirectory(_stateDirectory);

        // Write-then-move: a torn file reads as "nothing armed", never as half an entry.
        var temporaryPath = StatePath + ".tmp";
        await File.WriteAllBytesAsync(temporaryPath, JsonSerializer.SerializeToUtf8Bytes(restarts, Json), cancellationToken);
        File.Move(temporaryPath, StatePath, overwrite: true);
    }
}
