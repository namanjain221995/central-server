namespace EndpointPlatform.Contracts.Agent;

/// <summary>
/// One minute of activity presence: how long since the signed-in user last used the
/// keyboard or mouse. Presence only — never content, apps or screenshots.
/// </summary>
/// <param name="UserName">The signed-in user the minute is attributed to, e.g. <c>DOMAIN\jsmith</c>.</param>
/// <param name="SampledAt">The UTC minute this sample covers.</param>
/// <param name="IdleSeconds">Seconds since the last keyboard/mouse input at sample time.</param>
public sealed record ActivitySampleDto(string UserName, DateTimeOffset SampledAt, int IdleSeconds);

/// <summary>A batch of activity samples a device uploads; buffered offline and sent on the heartbeat.</summary>
public sealed record ActivityReportRequest(IReadOnlyList<ActivitySampleDto> Samples);
