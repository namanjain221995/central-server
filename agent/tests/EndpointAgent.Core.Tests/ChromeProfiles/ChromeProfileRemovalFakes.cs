using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.ChromeProfiles;

namespace EndpointAgent.Core.Tests.ChromeProfiles;

/// <summary>
/// The machine, faked: which SIDs resolve to a User Data directory, whether
/// Chrome is running and for whom, what Local State says, which profile
/// directories exist, and which operations fail how. Shared by the executor
/// tests and the deferred-removal worker tests, so both sides of the one engine
/// are pinned against the same seams.
/// </summary>
internal sealed class FakeChromeProfileRemover : IChromeProfileRemover
{
    public Dictionary<string, string> UserDataBySid { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ChromeRunningFor { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The Local State text reads return; null reads as unreadable.</summary>
    public string? LocalState { get; set; }

    /// <summary>Profile keys whose directory exists under User Data.</summary>
    public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Written { get; } = [];
    public List<string> Deleted { get; } = [];
    public Queue<Exception> WriteFailures { get; } = new();
    public Queue<Exception> DeleteFailures { get; } = new();
    public int WriteCalls { get; private set; }
    public int DeleteCalls { get; private set; }
    public int ChromeRunningChecks { get; private set; }

    public string? ResolveUserDataDirectory(string userSid) =>
        UserDataBySid.TryGetValue(userSid, out var directory) ? directory : null;

    public bool IsChromeRunningFor(string userSid)
    {
        ChromeRunningChecks++;
        return ChromeRunningFor.Contains(userSid);
    }

    public string? ReadLocalState(string userDataDirectory) => LocalState;

    public void WriteLocalState(string userDataDirectory, string json)
    {
        WriteCalls++;
        if (WriteFailures.Count > 0)
        {
            throw WriteFailures.Dequeue();
        }

        Written.Add(json);
        LocalState = json;
    }

    public bool ProfileDirectoryExists(string userDataDirectory, string profileKey) =>
        Directories.Contains(profileKey);

    public void DeleteProfileDirectory(string userDataDirectory, string profileKey)
    {
        DeleteCalls++;
        if (DeleteFailures.Count > 0)
        {
            throw DeleteFailures.Dequeue();
        }

        Directories.Remove(profileKey);
        Deleted.Add(profileKey);
    }
}

/// <summary>The pending store in memory, counting saves, so tests see exactly what would reach disk.</summary>
internal sealed class MemoryPendingChromeRemovalStore : IPendingChromeRemovalStore
{
    public List<PendingChromeProfileRemoval> Saved { get; private set; } = [];
    public int Saves { get; private set; }

    public ValueTask<IReadOnlyList<PendingChromeProfileRemoval>> LoadAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<PendingChromeProfileRemoval>>([.. Saved]);

    public ValueTask SaveAsync(IReadOnlyList<PendingChromeProfileRemoval> removals, CancellationToken cancellationToken = default)
    {
        Saved = [.. removals];
        Saves++;
        return ValueTask.CompletedTask;
    }
}
