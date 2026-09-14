namespace EndpointPlatform.Domain.Tasks;

/// <summary>
/// The rules for how long a device waits before restarting when an
/// administrator asks it to.
/// </summary>
/// <remarks>
/// <para>
/// One place, so the endpoint that validates a request, the payload the agent
/// receives and every test of the boundary agree on the same three numbers.
/// The value produced here is the <see cref="TaskPayloads.RestartOrShutdown.GraceSeconds"/>
/// the device hands to Windows, which counts it down itself from the moment the
/// task is executed. There is no other clock: the server does not compute a
/// deadline and the agent does not sleep.
/// </para>
/// <para>
/// <b>The maximum is the deployed agent's clamp, not a preference.</b> Every
/// agent in the field applies <c>Math.Clamp(graceSeconds, 0, 3600)</c> before
/// calling <c>InitiateSystemShutdownEx</c>. A server that queued a longer delay
/// would be telling the administrator one time and the machine another, and
/// the machine would win -- an endpoint going down an hour before it was said
/// to. So the server refuses anything the agent would shorten. Raising this
/// ceiling is an agent change first and a server change second, never the
/// other way round.
/// </para>
/// <para>
/// <b>"Now" is a thirty-second warning.</b> Zero is a request to restart
/// immediately, and the device honours it as the grace period a restart has
/// always had here: long enough for the signed-in user to save, and for the
/// agent to record the result before the machine goes down. Delays shorter
/// than that floor but longer than zero are refused rather than rounded up:
/// an administrator who typed 10 should be told 10 is not offered, not
/// silently given 30.
/// </para>
/// </remarks>
public static class RestartGrace
{
    /// <summary>What "restart now" means on the device: the warning it gives the signed-in user.</summary>
    public const int ImmediateSeconds = 30;

    /// <summary>The shortest explicit delay accepted. Equal to the immediate warning: nothing shorter is offered.</summary>
    public const int MinimumDelaySeconds = ImmediateSeconds;

    /// <summary>The longest delay accepted: exactly the clamp every deployed agent applies.</summary>
    public const int MaximumDelaySeconds = 3600;

    /// <summary>
    /// The grace period to queue for a requested delay, or null when the
    /// request is outside what is offered.
    /// </summary>
    /// <param name="delaySeconds">
    /// 0 for now; otherwise the number of seconds the device should wait once
    /// it has the task. Negative, sub-minimum and over-maximum values are all
    /// refused, and so is anything that overflows an <see cref="int"/> --
    /// the parameter type guarantees the last.
    /// </param>
    public static int? FromDelay(int delaySeconds)
    {
        if (delaySeconds == 0)
        {
            return ImmediateSeconds;
        }

        if (delaySeconds < MinimumDelaySeconds || delaySeconds > MaximumDelaySeconds)
        {
            return null;
        }

        return delaySeconds;
    }

    /// <summary>A grace period in words, for the message the signed-in user sees.</summary>
    public static string Describe(int graceSeconds)
    {
        if (graceSeconds < 60)
        {
            return $"{graceSeconds} seconds";
        }

        var minutes = graceSeconds / 60;
        var seconds = graceSeconds % 60;

        if (seconds == 0)
        {
            return minutes == 1 ? "1 minute" : $"{minutes} minutes";
        }

        return $"{minutes} minute{(minutes == 1 ? "" : "s")} {seconds} seconds";
    }

    /// <summary>
    /// The message Windows shows the signed-in user for a restart with this grace
    /// period. System-defined: no administrator-supplied text ever reaches it.
    /// </summary>
    /// <remarks>
    /// One source for both the single-device route and group restart, so a group
    /// restart delivers exactly the payload a single restart would -- which is
    /// the whole claim that a group action is just many ordinary device actions.
    /// </remarks>
    public static string MessageFor(int graceSeconds) =>
        graceSeconds == ImmediateSeconds
            ? "Your IT administrator initiated a restart."
            : $"Your IT administrator scheduled a restart in {Describe(graceSeconds)}.";

    /// <summary>The fixed shutdown message, shared by the single-device route and group shutdown.</summary>
    public const string ShutdownMessage = "Your IT administrator initiated a shutdown.";

    /// <summary>The fixed grace period for shutdown, as the single-device route has always used.</summary>
    public const int ShutdownGraceSeconds = ImmediateSeconds;
}
