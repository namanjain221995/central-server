using EndpointPlatform.Contracts.Agent;

namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// Reads what Google Chrome is installed on the machine, which profiles each
/// local user has, and which extensions each profile carries.
/// </summary>
/// <remarks>
/// <para>
/// Read-only, and structurally so: this interface has no counterpart that
/// installs, removes or configures anything in Chrome. Extension enforcement is
/// a separate, task-gated capability with its own abstraction, so nothing that
/// merely collects inventory can be widened into a mutation by accident.
/// </para>
/// <para>
/// The implementation reads the registry and files Chrome owns; it never writes
/// a profile file (Chrome authenticates its own preference files and treats an
/// outside edit as corruption) and never launches a process (ADR-0005). A file
/// Chrome holds open is read with shared access; one it is replacing at that
/// instant is simply missing from this snapshot, which the section's status and
/// the server's keep-last-known rule already allow for.
/// </para>
/// <para>
/// Per-user data is reached through the machine's profile list, not through the
/// service's own profile: the service runs as LocalSystem, whose
/// <c>AppData\Local</c> holds no one's Chrome.
/// </para>
/// </remarks>
public interface IChromeCollector
{
    ValueTask<InventoryChrome> CollectAsync(CancellationToken cancellationToken = default);
}
