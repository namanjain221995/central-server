using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Groups;

namespace EndpointPlatform.Domain.Tests.Groups;

public sealed class DeviceGroupTests
{
    private static readonly Guid Org = Guid.CreateVersion7();

    [Fact]
    public void All_Devices_is_built_in_and_carries_the_reserved_name()
    {
        var group = DeviceGroup.CreateAllDevices(Org);

        group.IsBuiltIn.ShouldBeTrue();
        group.Name.ShouldBe(DeviceGroup.AllDevicesName);
        group.Type.ShouldBe(DeviceGroupType.Static);
        group.OrganizationId.ShouldBe(Org);
    }

    [Fact]
    public void All_Devices_cannot_be_renamed()
    {
        var group = DeviceGroup.CreateAllDevices(Org);

        Should.Throw<InvalidOperationException>(() => group.Rename("Something Else"));
        group.Name.ShouldBe(DeviceGroup.AllDevicesName);
    }

    [Fact]
    public void All_Devices_cannot_be_deleted()
    {
        Should.Throw<InvalidOperationException>(() => DeviceGroup.CreateAllDevices(Org).EnsureDeletable());
    }

    [Fact]
    public void A_custom_group_is_not_built_in_and_may_be_renamed_and_deleted()
    {
        var group = new DeviceGroup(Org, "Developers", null, DeviceGroupType.Static);

        group.IsBuiltIn.ShouldBeFalse();
        Should.NotThrow(group.EnsureDeletable);

        group.Rename("Engineering");
        group.Name.ShouldBe("Engineering");
    }

    [Theory]
    [InlineData("All Devices")]
    [InlineData("all devices")]
    [InlineData("ALL DEVICES")]
    [InlineData("  All Devices  ")]
    public void No_custom_group_may_take_the_reserved_name_in_any_casing(string name)
    {
        Should.Throw<ArgumentException>(() => new DeviceGroup(Org, name, null, DeviceGroupType.Static));
        Should.Throw<ArgumentException>(() => new DeviceGroup(Org, "Fine", null, DeviceGroupType.Static).Rename(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Tab\tInside")]
    [InlineData("New\nLine")]
    [InlineData("Null\0Char")]
    public void Empty_or_control_character_names_are_refused(string name)
    {
        Should.Throw<ArgumentException>(() => new DeviceGroup(Org, name, null, DeviceGroupType.Static));
    }

    [Fact]
    public void A_name_is_trimmed_and_bounded()
    {
        new DeviceGroup(Org, "  Sales  ", null, DeviceGroupType.Static).Name.ShouldBe("Sales");
        Should.NotThrow(() => new DeviceGroup(Org, new string('x', DeviceGroup.MaxNameLength), null, DeviceGroupType.Static));
        Should.Throw<ArgumentException>(() =>
            new DeviceGroup(Org, new string('x', DeviceGroup.MaxNameLength + 1), null, DeviceGroupType.Static));
    }

    [Fact]
    public void A_description_is_optional_and_bounded()
    {
        new DeviceGroup(Org, "A", null, DeviceGroupType.Static).Description.ShouldBe(string.Empty);
        Should.Throw<ArgumentException>(() =>
            new DeviceGroup(Org, "A", new string('d', DeviceGroup.MaxDescriptionLength + 1), DeviceGroupType.Static));
    }

    [Fact]
    public void A_device_cannot_be_moved_to_the_empty_group()
    {
        var device = Device.Enroll(Org, "PC", "m-1", "1.9.0", null, Guid.CreateVersion7(), DateTimeOffset.UtcNow);

        Should.Throw<ArgumentException>(() => device.MoveToGroup(Guid.Empty));
    }

    /// <summary>A device holds exactly one group: moving replaces it, and there is nothing to add to.</summary>
    [Fact]
    public void Moving_a_device_replaces_its_group_rather_than_adding_one()
    {
        var device = Device.Enroll(Org, "PC", "m-2", "1.9.0", null, Guid.CreateVersion7(), DateTimeOffset.UtcNow);
        Guid first = Guid.CreateVersion7(), second = Guid.CreateVersion7();

        device.MoveToGroup(first);
        device.MoveToGroup(second);

        device.DeviceGroupId.ShouldBe(second);
    }
}
