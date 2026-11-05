using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.WorkTime;

/// <summary>
/// One minute of activity-presence reported by a device: how long, in seconds, it had
/// been since the signed-in user last touched the keyboard or mouse.
/// </summary>
/// <remarks>
/// This is presence only — whether there was input, never what was typed, which app, or
/// a screenshot. The server classifies a minute as active or idle by comparing
/// <see cref="IdleSeconds"/> against the organization's configurable threshold, so the
/// threshold can change without re-collecting anything. The organization is reached
/// through the device, not stored here (one join on report).
/// </remarks>
public sealed class DeviceActivitySample : AuditableEntity
{
    private DeviceActivitySample()
    {
    }

    public DeviceActivitySample(Guid deviceId, string userName, DateTimeOffset sampledAt, int idleSeconds)
    {
        DeviceId = Guard.NotEmpty(deviceId);
        UserName = Guard.NotNullOrWhiteSpace(userName, maxLength: 256);
        SampledAt = sampledAt;
        IdleSeconds = Math.Max(0, idleSeconds);
    }

    public Guid DeviceId { get; private set; }

    /// <summary>The signed-in user the sample is attributed to, e.g. <c>DOMAIN\jsmith</c>.</summary>
    public string UserName { get; private set; } = null!;

    /// <summary>The minute this sample covers (UTC, truncated to the minute).</summary>
    public DateTimeOffset SampledAt { get; private set; }

    /// <summary>Seconds since the last keyboard/mouse input at sample time.</summary>
    public int IdleSeconds { get; private set; }
}
