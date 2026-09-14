using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Tasks;

namespace EndpointPlatform.Domain.Tests.Tasks;

/// <summary>
/// The three numbers behind a timed restart, and the catalogue entry that
/// bounds how long a device has to pick one up.
/// </summary>
/// <remarks>
/// The maximum is not a taste: it is the clamp every deployed agent applies
/// before calling Windows. A server that accepted more would promise a later
/// restart than any endpoint delivers, so these tests pin the boundary rather
/// than merely exercise it.
/// </remarks>
public sealed class RestartGraceTests
{
    [Fact]
    public void Now_is_the_standard_warning_not_zero()
    {
        RestartGrace.FromDelay(0).ShouldBe(RestartGrace.ImmediateSeconds);
        RestartGrace.ImmediateSeconds.ShouldBe(30);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(31)]
    [InlineData(60)]
    [InlineData(900)]
    [InlineData(3599)]
    [InlineData(3600)]
    public void An_explicit_delay_within_range_is_queued_as_given(int delay) =>
        RestartGrace.FromDelay(delay).ShouldBe(delay);

    [Theory]
    [InlineData(-1)]
    [InlineData(-30)]
    [InlineData(int.MinValue)]
    public void A_negative_delay_is_refused(int delay) =>
        RestartGrace.FromDelay(delay).ShouldBeNull();

    /// <summary>Shorter than the warning but not zero: refused, never rounded up to the floor.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(29)]
    public void A_delay_below_the_floor_is_refused_rather_than_raised(int delay) =>
        RestartGrace.FromDelay(delay).ShouldBeNull();

    /// <summary>
    /// The ceiling is exactly the deployed agent's clamp. One second over is
    /// refused; so is anything that would have overflowed a narrower type.
    /// </summary>
    [Theory]
    [InlineData(3601)]
    [InlineData(86_400)]
    [InlineData(int.MaxValue)]
    public void A_delay_over_the_agent_clamp_is_refused(int delay) =>
        RestartGrace.FromDelay(delay).ShouldBeNull();

    [Fact]
    public void The_ceiling_is_the_agents_clamp()
    {
        // agent/EndpointAgent.Core/Tasks/BuiltInExecutors.cs ParseGrace clamps to
        // [0, 3600]; WindowsDeviceControl clamps again to the same. Raising this
        // number here without raising it there would promise restarts the
        // machine performs an hour early.
        RestartGrace.MaximumDelaySeconds.ShouldBe(3600);
        RestartGrace.MinimumDelaySeconds.ShouldBe(RestartGrace.ImmediateSeconds);
    }

    [Theory]
    [InlineData(30, "30 seconds")]
    [InlineData(59, "59 seconds")]
    [InlineData(60, "1 minute")]
    [InlineData(90, "1 minute 30 seconds")]
    [InlineData(120, "2 minutes")]
    [InlineData(150, "2 minutes 30 seconds")]
    [InlineData(3600, "60 minutes")]
    public void A_grace_period_is_described_in_plain_words(int seconds, string expected) =>
        RestartGrace.Describe(seconds).ShouldBe(expected);

    // ------------------------------------------------------------- catalogue

    [Fact]
    public void Restart_is_high_risk_gated_on_its_own_permission_and_expires_in_fifteen_minutes()
    {
        var definition = DeviceTaskCatalog.Require(DeviceTaskType.RestartDevice);

        definition.RequiredPermission.ShouldBe(Permissions.Device.Restart);
        definition.HighRisk.ShouldBeTrue();

        // The TTL bounds only how long the device has to *receive* the task; the
        // countdown itself is handed to Windows and reported back immediately, so
        // this never races the delay. Fifteen minutes is the existing budget.
        definition.DefaultTimeToLiveSeconds.ShouldBe(900);

        // No minimum agent version: every agent that has ever shipped executes
        // RestartDevice with a graceSeconds payload. The timed variant needs no
        // new executor, only a different number in the same field.
        definition.MinimumAgentVersion.ShouldBeNull();
    }

    [Fact]
    public void There_is_exactly_one_restart_task_type()
    {
        Enum.GetNames<DeviceTaskType>().Count(n => n.Contains("Restart", StringComparison.Ordinal))
            .ShouldBe(1, "a timed restart extends RestartDevice; it must not introduce a second restart type");
    }

    /// <summary>
    /// 24 was RemoveApplication and is retired. It must never be given to
    /// another task type.
    /// </summary>
    /// <remarks>
    /// Historic <c>device_tasks</c> rows and the audit entries copied from them
    /// still carry that value. Reusing it would make those records read as
    /// something that never happened -- an uninstall reported as whatever the
    /// new type is -- and no migration can fix that, because the rows are a
    /// truthful account of what was queued at the time.
    /// <para>
    /// The reservation was a comment until this test existed. A comment does not
    /// fail a build; a developer adding <c>Foo = 24</c> would have been told
    /// nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public void Task_type_24_stays_retired_and_is_never_reused()
    {
        var reused = Enum.GetValues<DeviceTaskType>()
            .Where(t => (int)t == 24)
            .Select(t => t.ToString())
            .ToArray();

        reused.ShouldBeEmpty(
            "24 was RemoveApplication and is reserved; historic task and audit rows still carry it. " +
            $"Give {string.Join(", ", reused)} a different number.");
    }
}
