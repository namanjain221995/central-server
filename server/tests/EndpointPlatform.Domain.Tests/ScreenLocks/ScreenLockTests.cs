using System.Text.Json;
using EndpointPlatform.Domain.ScreenLocks;
using EndpointPlatform.Domain.Tasks;

namespace EndpointPlatform.Domain.Tests.ScreenLocks;

/// <summary>
/// The rules of a screen lock: how much text a lock may carry, which target shapes
/// are coherent, what each device row records, and the wire contract the agent
/// relies on. The limits and the task catalogue are pinned here rather than
/// discovered on a locked screen.
/// </summary>
public sealed class ScreenLockTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid Group = Guid.CreateVersion7();
    private static readonly Guid Admin = Guid.CreateVersion7();

    private static ScreenLock ToGroup(
        string message = "This device is locked by IT.", string? contactLine = "Call the service desk on x1234.") =>
        ScreenLock.ToGroup(Org, message, contactLine, Group, Admin, "admin@test.local");

    // -------------------------------------------------------------------- text

    [Fact]
    public void Message_and_contact_line_are_trimmed_and_kept()
    {
        var screenLock = ScreenLock.ToDevices(Org, "  Locked  ", "  Call IT.  ", Admin, " admin@test.local ");

        screenLock.Message.ShouldBe("Locked");
        screenLock.ContactLine.ShouldBe("Call IT.");
        screenLock.CreatedByDisplay.ShouldBe("admin@test.local");
        screenLock.TargetType.ShouldBe(ScreenLockTargetType.Devices);
        screenLock.DeviceGroupId.ShouldBeNull();
    }

    [Fact]
    public void A_blank_contact_line_normalises_to_null()
    {
        ScreenLock.ToDevices(Org, "Locked", null, Admin, "admin@test.local").ContactLine.ShouldBeNull();
        ScreenLock.ToDevices(Org, "Locked", "   ", Admin, "admin@test.local").ContactLine.ShouldBeNull();
    }

    [Fact]
    public void The_longest_allowed_message_and_contact_line_are_accepted()
    {
        var screenLock = ToGroup(new string('m', 500), new string('c', 200));

        screenLock.Message.Length.ShouldBe(500);
        screenLock.ContactLine!.Length.ShouldBe(200);
        ScreenLock.MaxMessageLength.ShouldBe(500);
        ScreenLock.MaxContactLineLength.ShouldBe(200);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_message_is_refused(string? text) =>
        Should.Throw<ArgumentException>(() => ToGroup(message: text!));

    [Fact]
    public void Text_over_the_limits_is_refused_not_truncated()
    {
        Should.Throw<ArgumentException>(() => ToGroup(message: new string('m', 501)));
        Should.Throw<ArgumentException>(() => ToGroup(contactLine: new string('c', 201)));
        Should.Throw<ArgumentException>(() =>
            ScreenLock.ToGroup(Org, "Locked", null, Group, Admin, new string('d', 257)));
    }

    /// <summary>
    /// The server-side mirror of the agent's lock-screen protocol: control
    /// characters other than line breaks and tabs are refused, so text the server
    /// accepted could not queue a lock fleet-wide that then fails on every device.
    /// </summary>
    [Fact]
    public void Control_characters_other_than_line_breaks_and_tabs_are_refused()
    {
        Should.Throw<ArgumentException>(() => ToGroup(message: "Bell \u0007 message"));
        Should.Throw<ArgumentException>(() => ToGroup(contactLine: "Null \0 contact"));
        Should.Throw<ArgumentException>(() => ToGroup(message: "C1 \u0085 control"));

        var multiline = ToGroup(message: "Line one.\r\nLine two.\tIndented.");
        multiline.Message.ShouldBe("Line one.\r\nLine two.\tIndented.");

        ScreenLock.IsPlainText(null).ShouldBeFalse();
        ScreenLock.IsPlainText("plain text").ShouldBeTrue();
        ScreenLock.IsPlainText("tab\tand\nnewline\r").ShouldBeTrue();
        ScreenLock.IsPlainText("bell\u0007").ShouldBeFalse();
    }

    // ----------------------------------------------------------------- targets

    [Fact]
    public void A_group_lock_names_its_group_and_a_device_one_does_not()
    {
        var grouped = ToGroup();
        grouped.TargetType.ShouldBe(ScreenLockTargetType.Group);
        grouped.DeviceGroupId.ShouldBe(Group);

        var devices = ScreenLock.ToDevices(Org, "Locked", null, Admin, "admin@test.local");
        devices.TargetType.ShouldBe(ScreenLockTargetType.Devices);
        devices.DeviceGroupId.ShouldBeNull();

        Should.Throw<ArgumentException>(() =>
            ScreenLock.ToGroup(Org, "Locked", null, Guid.Empty, Admin, "admin@test.local"));
    }

    /// <summary>
    /// Stored as text, so the ordinals only matter to code -- but the names are what
    /// history rows carry, and renaming one would reinterpret them.
    /// </summary>
    [Fact]
    public void The_target_type_names_and_ordinals_are_pinned()
    {
        ((int)ScreenLockTargetType.Group).ShouldBe(0);
        ((int)ScreenLockTargetType.Devices).ShouldBe(1);
        ScreenLockTargetType.Group.ToString().ShouldBe("Group");
        ScreenLockTargetType.Devices.ToString().ShouldBe("Devices");
    }

    // ------------------------------------------------------------- device rows

    [Fact]
    public void A_device_row_carries_its_engage_task_or_its_skip_reason_never_both_or_neither()
    {
        var lockId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();

        var queued = new ScreenLockDevice(lockId, Guid.CreateVersion7(), "PC-1", taskId, skipReason: null);
        queued.EngageTaskId.ShouldBe(taskId);
        queued.SkipReason.ShouldBeNull();
        queued.ReleaseTaskId.ShouldBeNull();

        var skipped = new ScreenLockDevice(
            lockId, Guid.CreateVersion7(), "PC-2", engageTaskId: null, ScreenLockDevice.SkipReasonRetired);
        skipped.EngageTaskId.ShouldBeNull();
        skipped.SkipReason.ShouldBe("Retired");

        Should.Throw<ArgumentException>(() =>
            new ScreenLockDevice(lockId, Guid.CreateVersion7(), "PC-3", taskId, "Retired"));
        Should.Throw<ArgumentException>(() =>
            new ScreenLockDevice(lockId, Guid.CreateVersion7(), "PC-4", engageTaskId: null, skipReason: null));
    }

    [Fact]
    public void Only_an_engaged_row_can_be_released_and_only_once()
    {
        var engaged = new ScreenLockDevice(Guid.CreateVersion7(), Guid.CreateVersion7(), "PC", Guid.CreateVersion7(), null);
        var release = Guid.CreateVersion7();

        engaged.TryMarkReleased(release).ShouldBeTrue();
        engaged.ReleaseTaskId.ShouldBe(release);
        // A second release is a no-op; the task that actually lifted it stands.
        engaged.TryMarkReleased(Guid.CreateVersion7()).ShouldBeFalse();
        engaged.ReleaseTaskId.ShouldBe(release);

        var skipped = new ScreenLockDevice(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "PC", engageTaskId: null, ScreenLockDevice.SkipReasonAgentTooOld);
        skipped.TryMarkReleased(Guid.CreateVersion7()).ShouldBeFalse();
        skipped.ReleaseTaskId.ShouldBeNull();
    }

    // ------------------------------------------------------------ the catalogue

    [Fact]
    public void Engage_is_high_risk_lives_a_week_and_needs_the_lock_executor()
    {
        var definition = DeviceTaskCatalog.Require(DeviceTaskType.EngageScreenLock);

        ((int)DeviceTaskType.EngageScreenLock).ShouldBe(52);
        DeviceTaskType.EngageScreenLock.ToString().ShouldBe("EngageScreenLock");
        definition.RequiredPermission.ShouldBe(Domain.Authorization.Permissions.Device.ScreenLock);
        definition.HighRisk.ShouldBeTrue("it takes a machine over completely until it is deliberately released");
        definition.DefaultTimeToLiveSeconds.ShouldBe(7 * 24 * 3600,
            "an offline device should still come up locked on reconnect, up to a week");
        definition.MinimumAgentVersion.ShouldBe("1.19.1");
        DeviceTaskCatalog.IsSupportedBy(definition, "1.18.9").ShouldBeFalse();
        DeviceTaskCatalog.IsSupportedBy(definition, "1.19.0").ShouldBeFalse(
            "the 1.19.0 overlay never saw its Ctrl+Alt+U chord; a device it locked had no on-device way out");
        DeviceTaskCatalog.IsSupportedBy(definition, "1.19.1").ShouldBeTrue();
        DeviceTaskCatalog.IsSupportedBy(definition, "1.19.2+ci").ShouldBeTrue();
        DeviceTaskCatalog.IsSupportedBy(definition, null).ShouldBeFalse();
    }

    [Fact]
    public void Release_lifts_a_lock_so_it_is_not_high_risk_but_shares_the_permission_and_lifetime()
    {
        var definition = DeviceTaskCatalog.Require(DeviceTaskType.ReleaseScreenLock);

        ((int)DeviceTaskType.ReleaseScreenLock).ShouldBe(53);
        DeviceTaskType.ReleaseScreenLock.ToString().ShouldBe("ReleaseScreenLock");
        definition.RequiredPermission.ShouldBe(Domain.Authorization.Permissions.Device.ScreenLock);
        definition.HighRisk.ShouldBeFalse("it lifts a lock rather than imposing one");
        definition.DefaultTimeToLiveSeconds.ShouldBe(7 * 24 * 3600);
        definition.MinimumAgentVersion.ShouldBe("1.19.0");
        // Deliberately below the engage's minimum: a device locked while on 1.19.0
        // must still be releasable from the dashboard, its only way out.
        DeviceTaskCatalog.IsSupportedBy(definition, "1.19.0").ShouldBeTrue();
        DeviceTaskCatalog.IsSupportedBy(DeviceTaskCatalog.Require(DeviceTaskType.EngageScreenLock), "1.19.0").ShouldBeFalse();
    }

    // -------------------------------------------------------------- wire format

    /// <summary>
    /// The agent reads exactly these camelCase keys, and the verifier never carries
    /// the plaintext -- only its PBKDF2 parameters. A rename here is a wire break
    /// however green the server's own tests stay.
    /// </summary>
    [Fact]
    public void The_engage_payload_serializes_to_the_camel_case_contract_carrying_the_verifier_not_the_code()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var verifier = new TaskPayloads.ScreenLockVerifier("pbkdf2-sha256", 210000, "c2FsdA==", "aGFzaA==");
        var payload = new TaskPayloads.EngageScreenLock(id, "This device is locked by IT.", "Call x1234.", verifier);

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        json.ShouldBe(
            """{"lockId":"11111111-2222-3333-4444-555555555555","message":"This device is locked by IT.","contactLine":"Call x1234.","unlock":{"algo":"pbkdf2-sha256","iterations":210000,"salt":"c2FsdA==","hash":"aGFzaA=="}}""");
    }

    [Fact]
    public void A_missing_contact_line_still_serializes_the_key_so_the_agent_can_read_it()
    {
        var verifier = new TaskPayloads.ScreenLockVerifier("pbkdf2-sha256", 210000, "c2FsdA==", "aGFzaA==");
        var payload = new TaskPayloads.EngageScreenLock(
            Guid.Parse("11111111-2222-3333-4444-555555555555"), "Locked.", null, verifier);

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        json.ShouldContain("\"contactLine\":null");
    }

    [Fact]
    public void The_release_payload_names_only_the_lock()
    {
        var payload = new TaskPayloads.ReleaseScreenLock(Guid.Parse("11111111-2222-3333-4444-555555555555"));

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        json.ShouldBe("""{"lockId":"11111111-2222-3333-4444-555555555555"}""");
    }
}
