namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// Reads the installed remote-support client's device identifier (the RustDesk ID)
/// so the agent can report it on the heartbeat.
/// </summary>
/// <remarks>
/// The RustDesk ID is the number an administrator dials to reach this device for
/// remote support. It is an identifier, never a secret, so it is safe to carry on
/// the heartbeat. The reader is read-only: it reads RustDesk's own configuration and
/// runs no process (ADR-0005).
/// </remarks>
public interface IRemoteSupportReader
{
    /// <summary>
    /// The installed RustDesk's ID, or null when RustDesk is not installed or has not
    /// yet registered with the rendezvous server (so no ID exists to report).
    /// </summary>
    string? ReadRustDeskId();
}
