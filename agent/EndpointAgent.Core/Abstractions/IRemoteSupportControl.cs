namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// The outcome of configuring remote support on this device: whether it took, and a
/// short, human-readable, secret-free message for the task result.
/// </summary>
/// <param name="Succeeded">Whether the unattended password was applied.</param>
/// <param name="Message">A short outcome or failure reason. Never carries the password.</param>
public sealed record RemoteSupportConfigureResult(bool Succeeded, string? Message);

/// <summary>
/// Configures the device's already-installed remote-support client (RustDesk) with a
/// platform-managed unattended password, so an authorized IT operator can start a
/// remote-control session.
/// </summary>
/// <remarks>
/// <para>
/// RustDesk is installed separately (through Company Apps); this does NOT install it.
/// It only sets the unattended (permanent) password on an install that is already
/// present, and reports whether it could.
/// </para>
/// <para>
/// The password is a one-time secret redeemed immediately before this call. The
/// implementation must never log it, never persist it, and never echo it back in the
/// <see cref="RemoteSupportConfigureResult"/>.
/// </para>
/// </remarks>
public interface IRemoteSupportControl
{
    /// <summary>
    /// Sets RustDesk's unattended password. Returns a failure result (rather than
    /// throwing) when RustDesk is not installed or the password is unusable.
    /// </summary>
    Task<RemoteSupportConfigureResult> SetUnattendedPasswordAsync(string password, CancellationToken ct);
}
