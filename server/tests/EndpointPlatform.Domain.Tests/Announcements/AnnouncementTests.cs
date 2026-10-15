using System.Text.Json;
using EndpointPlatform.Domain.Announcements;
using EndpointPlatform.Domain.Tasks;

namespace EndpointPlatform.Domain.Tests.Announcements;

/// <summary>
/// The rules of an announcement: how much text a broadcast may carry, which
/// target shapes are coherent, and what each device row must record. The limits
/// are the contract the agent's display executor relies on, so they are pinned
/// here rather than discovered on a screen.
/// </summary>
public sealed class AnnouncementTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid Group = Guid.CreateVersion7();
    private static readonly Guid Admin = Guid.CreateVersion7();

    private static Announcement ToGroup(string title = "Maintenance tonight", string message = "Save your work by 18:00.") =>
        Announcement.ToGroup(Org, title, message, Group, Admin, "admin@test.local");

    // -------------------------------------------------------------------- text

    [Fact]
    public void Title_and_message_are_trimmed_and_kept()
    {
        var announcement = Announcement.ToDevices(Org, "  Heads up  ", "  Mind the gap.  ", Admin, " admin@test.local ");

        announcement.Title.ShouldBe("Heads up");
        announcement.Message.ShouldBe("Mind the gap.");
        announcement.CreatedByDisplay.ShouldBe("admin@test.local");
        announcement.TargetType.ShouldBe(AnnouncementTargetType.Devices);
        announcement.DeviceGroupId.ShouldBeNull();
    }

    [Fact]
    public void The_longest_allowed_title_and_message_are_accepted()
    {
        var announcement = ToGroup(new string('t', 80), new string('m', 500));

        announcement.Title.Length.ShouldBe(80);
        announcement.Message.Length.ShouldBe(500);
        Announcement.MaxTitleLength.ShouldBe(80);
        Announcement.MaxMessageLength.ShouldBe(500);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_title_or_message_is_refused(string? text)
    {
        Should.Throw<ArgumentException>(() => ToGroup(title: text!));
        Should.Throw<ArgumentException>(() => ToGroup(message: text!));
    }

    [Fact]
    public void Text_over_the_limits_is_refused_not_truncated()
    {
        Should.Throw<ArgumentException>(() => ToGroup(title: new string('t', 81)));
        Should.Throw<ArgumentException>(() => ToGroup(message: new string('m', 501)));
        Should.Throw<ArgumentException>(() =>
            Announcement.ToGroup(Org, "Hi", "There", Group, Admin, new string('d', 257)));
    }

    /// <summary>
    /// The server-side mirror of the agent's
    /// <c>SessionNoticeProtocol.IsValidTitle</c>/<c>IsValidMessage</c>: the
    /// display executor refuses control characters other than line breaks and
    /// tabs, so text the server accepted would otherwise queue fleet-wide and
    /// fail on every device.
    /// </summary>
    [Fact]
    public void Control_characters_other_than_line_breaks_and_tabs_are_refused()
    {
        Should.Throw<ArgumentException>(() => ToGroup(title: "Bell \u0007 title"));
        Should.Throw<ArgumentException>(() => ToGroup(message: "Null \0 message"));
        Should.Throw<ArgumentException>(() => ToGroup(message: "C1 \u0085 control"));

        var multiline = ToGroup(message: "Line one.\r\nLine two.\tIndented.");
        multiline.Message.ShouldBe("Line one.\r\nLine two.\tIndented.");

        Announcement.IsPlainText(null).ShouldBeFalse();
        Announcement.IsPlainText("plain text").ShouldBeTrue();
        Announcement.IsPlainText("tab\tand\nnewline\r").ShouldBeTrue();
        Announcement.IsPlainText("bell\u0007").ShouldBeFalse();
    }

    // ----------------------------------------------------------------- targets

    [Fact]
    public void A_group_announcement_names_its_group_and_a_device_one_does_not()
    {
        var grouped = ToGroup();
        grouped.TargetType.ShouldBe(AnnouncementTargetType.Group);
        grouped.DeviceGroupId.ShouldBe(Group);

        Should.Throw<ArgumentException>(() =>
            Announcement.ToGroup(Org, "Hi", "There", Guid.Empty, Admin, "admin@test.local"));
    }

    /// <summary>
    /// Stored as text, so the ordinals only matter to code -- but the names are
    /// what history rows carry, and renaming one would reinterpret them.
    /// </summary>
    [Fact]
    public void The_target_type_names_and_ordinals_are_pinned()
    {
        ((int)AnnouncementTargetType.Group).ShouldBe(0);
        ((int)AnnouncementTargetType.Devices).ShouldBe(1);
        AnnouncementTargetType.Group.ToString().ShouldBe("Group");
        AnnouncementTargetType.Devices.ToString().ShouldBe("Devices");
    }

    // ------------------------------------------------------------- device rows

    [Fact]
    public void A_device_row_carries_its_task_or_its_skip_reason_never_both_or_neither()
    {
        var announcementId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();

        var queued = new AnnouncementDevice(announcementId, Guid.CreateVersion7(), "PC-1", taskId, skipReason: null);
        queued.TaskId.ShouldBe(taskId);
        queued.SkipReason.ShouldBeNull();

        var skipped = new AnnouncementDevice(
            announcementId, Guid.CreateVersion7(), "PC-2", taskId: null, AnnouncementDevice.SkipReasonRetired);
        skipped.TaskId.ShouldBeNull();
        skipped.SkipReason.ShouldBe("Retired");

        Should.Throw<ArgumentException>(() =>
            new AnnouncementDevice(announcementId, Guid.CreateVersion7(), "PC-3", taskId, "Retired"));
        Should.Throw<ArgumentException>(() =>
            new AnnouncementDevice(announcementId, Guid.CreateVersion7(), "PC-4", taskId: null, skipReason: null));
    }

    [Fact]
    public void A_device_row_validates_its_text()
    {
        var announcementId = Guid.CreateVersion7();

        Should.Throw<ArgumentException>(() =>
            new AnnouncementDevice(announcementId, Guid.CreateVersion7(), " ", Guid.CreateVersion7(), null));
        Should.Throw<ArgumentException>(() =>
            new AnnouncementDevice(announcementId, Guid.CreateVersion7(), new string('h', 257), Guid.CreateVersion7(), null));
        Should.Throw<ArgumentException>(() =>
            new AnnouncementDevice(announcementId, Guid.CreateVersion7(), "PC-5", null, new string('r', 65)));
    }

    // ------------------------------------------------------------ the catalogue

    [Fact]
    public void The_show_announcement_task_is_high_risk_lives_a_day_and_needs_the_display_executor()
    {
        var definition = DeviceTaskCatalog.Require(DeviceTaskType.ShowAnnouncement);

        ((int)DeviceTaskType.ShowAnnouncement).ShouldBe(51);
        definition.RequiredPermission.ShouldBe(Domain.Authorization.Permissions.Announcement.Send);
        definition.HighRisk.ShouldBeTrue("it interrupts every signed-in user on every targeted machine at once");
        definition.DefaultTimeToLiveSeconds.ShouldBe(24 * 3600,
            "an offline device should still show the notice on reconnect, up to a day");
        definition.MinimumAgentVersion.ShouldBe("1.18.0");
        DeviceTaskCatalog.IsSupportedBy(definition, "1.17.9").ShouldBeFalse();
        DeviceTaskCatalog.IsSupportedBy(definition, "1.18.0").ShouldBeTrue();
        DeviceTaskCatalog.IsSupportedBy(definition, "1.18.2+ci").ShouldBeTrue();
        DeviceTaskCatalog.IsSupportedBy(definition, null).ShouldBeFalse();
    }

    // -------------------------------------------------------------- wire format

    /// <summary>
    /// The agent reads exactly these camelCase keys; a rename here is a wire
    /// break however green the server's own tests stay.
    /// </summary>
    [Fact]
    public void The_payload_serializes_to_the_camel_case_contract_the_agent_reads()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var payload = new TaskPayloads.ShowAnnouncement(id, "Maintenance tonight", "Save your work by 18:00.");

        // The same serializer defaults DeviceTaskService uses for every payload.
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        json.ShouldBe(
            """{"announcementId":"11111111-2222-3333-4444-555555555555","title":"Maintenance tonight","message":"Save your work by 18:00."}""");
    }
}
