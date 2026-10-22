using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Tasks;

namespace EndpointPlatform.Domain.Tests.Tasks;

/// <summary>
/// The Chrome extension policy task's catalogue entry and agent gate.
/// </summary>
/// <remarks>
/// The entry is the authorization decision for the whole feature's write side --
/// which permission queues the task, whether the UI must confirm it, and which
/// agents may receive it -- so it is pinned the way the driver task's entry is,
/// rather than left to whatever a future edit happens to type.
/// </remarks>
public sealed class ChromeExtensionPolicyCatalogTests
{
    private static DeviceTaskDefinition Policy =>
        DeviceTaskCatalog.Require(DeviceTaskType.ApplyChromeExtensionPolicy);

    /// <summary>
    /// The enum number is part of history: it appears in device_tasks rows and the
    /// audit entries copied from them, so it can never be reused or renumbered.
    /// </summary>
    [Fact]
    public void The_task_type_keeps_its_wire_number()
    {
        ((int)DeviceTaskType.ApplyChromeExtensionPolicy).ShouldBe(59);
    }

    [Fact]
    public void Applying_extension_policy_requires_chrome_manage_and_is_high_risk()
    {
        Policy.RequiredPermission.ShouldBe(Permissions.Chrome.Manage);
        Policy.HighRisk.ShouldBeTrue(
            "a rule decides what software runs inside every signed-in browser session on the machine");
    }

    [Fact]
    public void The_policy_task_lives_an_hour()
    {
        Policy.DefaultTimeToLiveSeconds.ShouldBe(3600);
    }

    [Fact]
    public void The_policy_task_is_gated_on_the_agent_that_has_the_executor()
    {
        Policy.MinimumAgentVersion.ShouldBe("1.23.0");
    }

    [Theory]
    [InlineData("1.23.0")]
    [InlineData("1.23.1")]
    [InlineData("1.24.0")]
    [InlineData("2.0.0")]
    public void An_agent_at_or_above_the_minimum_is_supported(string agentVersion)
    {
        DeviceTaskCatalog.IsSupportedBy(Policy, agentVersion).ShouldBeTrue();
    }

    /// <summary>
    /// Includes the versions actually on this fleet today (1.22.0 and below), which
    /// is why the server must refuse rather than queue a task nothing will run.
    /// </summary>
    [Theory]
    [InlineData("1.22.9")]
    [InlineData("1.22.0")]
    [InlineData("1.18.0")]
    [InlineData("1.16.2")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    public void An_agent_below_the_minimum_or_unparseable_is_refused(string? agentVersion)
    {
        DeviceTaskCatalog.IsSupportedBy(Policy, agentVersion).ShouldBeFalse();
    }
}
