using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Tasks;

namespace EndpointPlatform.Domain.Tests.Tasks;

/// <summary>
/// The Chrome profile removal task's catalogue entry and agent gate.
/// </summary>
/// <remarks>
/// The entry is the authorization decision for the feature's destructive half --
/// which permission queues the removal, that the UI must confirm it, and which
/// agents may receive it -- so it is pinned the way the extension policy's entry
/// is, rather than left to whatever a future edit happens to type.
/// </remarks>
public sealed class ChromeProfileRemovalCatalogTests
{
    private static DeviceTaskDefinition Removal =>
        DeviceTaskCatalog.Require(DeviceTaskType.RemoveChromeProfile);

    /// <summary>
    /// The enum number is part of history: it appears in device_tasks rows and the
    /// audit entries copied from them, so it can never be reused or renumbered.
    /// </summary>
    [Fact]
    public void The_task_type_keeps_its_wire_number()
    {
        ((int)DeviceTaskType.RemoveChromeProfile).ShouldBe(62);
    }

    [Fact]
    public void Removing_a_profile_requires_chrome_manage_and_is_high_risk()
    {
        Removal.RequiredPermission.ShouldBe(Permissions.Chrome.Manage);
        Removal.HighRisk.ShouldBeTrue(
            "deleting a profile irreversibly deletes a person's bookmarks, history and sign-in state");
    }

    [Fact]
    public void The_removal_task_lives_an_hour()
    {
        Removal.DefaultTimeToLiveSeconds.ShouldBe(3600);
    }

    [Fact]
    public void The_removal_task_is_gated_on_the_agent_that_has_the_executor()
    {
        Removal.MinimumAgentVersion.ShouldBe("1.25.0");
    }

    [Theory]
    [InlineData("1.25.0")]
    [InlineData("1.25.1")]
    [InlineData("1.26.0")]
    [InlineData("2.0.0")]
    public void An_agent_at_or_above_the_minimum_is_supported(string agentVersion)
    {
        DeviceTaskCatalog.IsSupportedBy(Removal, agentVersion).ShouldBeTrue();
    }

    /// <summary>
    /// Includes the versions actually on this fleet today (1.24.0 and below), which
    /// is why the server must refuse rather than queue a task nothing will run --
    /// an older agent failing it as an unknown type would read exactly like the
    /// removal failing, and the operator would retry a thing that cannot work.
    /// </summary>
    [Theory]
    [InlineData("1.24.9")]
    [InlineData("1.24.0")]
    [InlineData("1.23.0")]
    [InlineData("1.18.0")]
    [InlineData("1.16.2")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    public void An_agent_below_the_minimum_or_unparseable_is_refused(string? agentVersion)
    {
        DeviceTaskCatalog.IsSupportedBy(Removal, agentVersion).ShouldBeFalse();
    }
}
