using System.Collections.Immutable;
using EndpointPlatform.Domain.Tasks;

namespace EndpointPlatform.Domain.Restarts;

/// <summary>
/// The rules for "restart this department after ...": which delays are
/// accepted, and how much of a delay is handed to Windows as the warning the
/// signed-in user sees.
/// </summary>
/// <remarks>
/// <para>
/// A scheduled restart has two clocks, deliberately. The server holds the
/// schedule and does nothing until <c>RestartAt - warning</c>; only then does it
/// queue the ordinary restart task with the warning as its grace period, and
/// from there Windows counts down exactly as for any other restart. Up to that
/// moment nothing has reached a device, so cancelling is just deleting the
/// schedule; after it, cancelling means <see cref="DeviceTaskType.CancelRestart"/>.
/// </para>
/// <para>
/// The warning is the configured period, or the whole delay when the delay is
/// shorter -- "restart in 2 minutes" hands Windows 2 minutes and goes out at
/// once. It is clamped to what every deployed agent accepts for a grace period.
/// </para>
/// </remarks>
public static class RestartScheduleDelay
{
    /// <summary>The choices the console offers, in seconds.</summary>
    public static readonly ImmutableArray<int> PresetSeconds = [60, 120, 300, 600, 900, 1800, 3600, 7200];

    /// <summary>Nothing shorter is a schedule: the immediate restart exists for that.</summary>
    public const int MinimumSeconds = 60;

    /// <summary>Seven days. Longer than any maintenance window; short enough to still be a plan, not a forgotten one.</summary>
    public const int MaximumSeconds = 7 * 24 * 3600;

    /// <summary>Whether a requested delay is one the server accepts.</summary>
    public static bool IsAccepted(int delaySeconds) =>
        delaySeconds >= MinimumSeconds && delaySeconds <= MaximumSeconds;

    /// <summary>
    /// The grace period handed to Windows for a schedule with this delay: the
    /// configured warning, or the whole delay when that is shorter. Always
    /// within what the agent accepts.
    /// </summary>
    public static int WarningFor(int delaySeconds, int configuredWarningSeconds)
    {
        var warning = Math.Clamp(configuredWarningSeconds, RestartGrace.MinimumDelaySeconds, RestartGrace.MaximumDelaySeconds);
        return Math.Min(delaySeconds, warning);
    }

    /// <summary>"2 minutes", "1 hour 30 minutes", "2 days", "1 day 3 hours 5 minutes".</summary>
    public static string Describe(int seconds)
    {
        if (seconds < 60)
        {
            return $"{seconds} second{(seconds == 1 ? "" : "s")}";
        }

        var days = seconds / 86_400;
        var hours = seconds % 86_400 / 3600;
        var minutes = seconds % 3600 / 60;

        var parts = new List<string>(3);
        if (days > 0) parts.Add($"{days} day{(days == 1 ? "" : "s")}");
        if (hours > 0) parts.Add($"{hours} hour{(hours == 1 ? "" : "s")}");
        if (minutes > 0) parts.Add($"{minutes} minute{(minutes == 1 ? "" : "s")}");
        return string.Join(" ", parts);
    }
}
