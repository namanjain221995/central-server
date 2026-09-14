using EndpointAgent.Core.SessionNotice;

namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// Tells signed-in users that a restart Windows has accepted is on its way.
/// </summary>
/// <remarks>
/// <para>
/// Informational only, and never the thing that restarts anything: Windows owns
/// the countdown the moment it accepts the request, and it shows its own
/// shutdown warning regardless. A notifier that fails, or is absent, must not
/// change the restart or its reported result -- which is why
/// <c>RestartTaskExecutor</c> calls it only after Windows has said yes, and
/// swallows anything it throws.
/// </para>
/// <para>
/// There is no "cancelled" notice. A restart is cancellable only before it is
/// delivered, and an undelivered restart was never announced; once Windows has
/// it, this platform cannot take it back.
/// </para>
/// </remarks>
public interface IRestartNotifier
{
    void RestartScheduled(RestartNotice notice);
}

/// <summary>Used where there is no session to tell -- and in every test that must not touch one.</summary>
public sealed class NullRestartNotifier : IRestartNotifier
{
    public static readonly NullRestartNotifier Instance = new();

    public void RestartScheduled(RestartNotice notice)
    {
    }
}
