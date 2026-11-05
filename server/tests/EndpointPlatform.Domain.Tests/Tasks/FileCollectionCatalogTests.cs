using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Tasks;

namespace EndpointPlatform.Domain.Tests.Tasks;

/// <summary>
/// The file-collection task's catalogue entry and agent gate.
/// </summary>
/// <remarks>
/// The entry is the authorization decision for a capability that downloads an
/// employee's personal files, so it is pinned the same way the Chrome removal and
/// security-update entries are, rather than left to whatever a future edit types.
/// </remarks>
public sealed class FileCollectionCatalogTests
{
    private static DeviceTaskDefinition Collect =>
        DeviceTaskCatalog.Require(DeviceTaskType.CollectFiles);

    /// <summary>
    /// The enum number appears in device_tasks rows and the audit entries copied
    /// from them, so it can never be reused or renumbered.
    /// </summary>
    [Fact]
    public void The_task_type_keeps_its_wire_number()
    {
        ((int)DeviceTaskType.CollectFiles).ShouldBe(63);
    }

    [Fact]
    public void Collecting_files_requires_files_collect_and_is_high_risk()
    {
        Collect.RequiredPermission.ShouldBe(Permissions.Files.Collect);
        Collect.HighRisk.ShouldBeTrue("it downloads an employee's personal files");
    }

    /// <summary>
    /// A long execution grace: zipping and uploading a Documents folder over a slow
    /// uplink can take a long time, and a claim late in the TTL must not let the
    /// sweeper expire the task mid-transfer and refuse the device's honest result.
    /// </summary>
    [Fact]
    public void The_collection_task_has_a_long_execution_grace()
    {
        Collect.DefaultTimeToLiveSeconds.ShouldBe(3600);
        Collect.ExecutionGraceSeconds.ShouldBe(10800);
    }

    /// <summary>
    /// Gated on the agent that carries the collection executor; an older one would
    /// fail it as an unknown type, which reads exactly like the collection failing.
    /// </summary>
    [Fact]
    public void The_collection_task_is_gated_on_the_agent_with_the_executor()
    {
        Collect.MinimumAgentVersion.ShouldBe("1.29.0");
        DeviceTaskCatalog.IsSupportedBy(Collect, "1.28.0").ShouldBeFalse();
        DeviceTaskCatalog.IsSupportedBy(Collect, "1.29.0").ShouldBeTrue();
        DeviceTaskCatalog.IsSupportedBy(Collect, null).ShouldBeFalse();
    }

    private static DeviceTaskDefinition List =>
        DeviceTaskCatalog.Require(DeviceTaskType.ListUserFolder);

    [Fact]
    public void Listing_keeps_its_wire_number_and_rides_files_collect()
    {
        ((int)DeviceTaskType.ListUserFolder).ShouldBe(64);
        List.RequiredPermission.ShouldBe(Permissions.Files.Collect);
    }

    [Fact]
    public void Listing_is_not_high_risk_and_keeps_its_deadline()
    {
        // It only reveals names and sizes — no content leaves the device — and returns
        // quickly, so unlike the collection it is neither high-risk nor granted grace.
        List.HighRisk.ShouldBeFalse();
        List.ExecutionGraceSeconds.ShouldBe(0);
    }

    [Fact]
    public void Listing_is_gated_on_the_1_30_0_agent()
    {
        List.MinimumAgentVersion.ShouldBe("1.30.0");
        DeviceTaskCatalog.IsSupportedBy(List, "1.29.0").ShouldBeFalse();
        DeviceTaskCatalog.IsSupportedBy(List, "1.30.0").ShouldBeTrue();
    }
}
