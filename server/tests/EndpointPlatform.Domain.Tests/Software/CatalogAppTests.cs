using EndpointPlatform.Domain.Software;

namespace EndpointPlatform.Domain.Tests.Software;

/// <summary>
/// The Company Portal catalog entry: what an administrator may set on it, and
/// the one question the portal asks of it -- may this device see this app.
/// </summary>
public sealed class CatalogAppTests
{
    private static readonly Guid Creator = Guid.CreateVersion7();

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static CatalogApp Create(
        string displayName = "Zoom",
        string? description = "Video calls",
        string? category = "Communication",
        CatalogVisibility visibility = CatalogVisibility.AllDevices) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), displayName, description, category,
            visibility, Creator, "admin@test");

    private static byte[] Png(int payloadBytes = 4) =>
        [.. PngSignature, .. Enumerable.Repeat((byte)0x2A, payloadBytes)];

    // ------------------------------------------------------------ creation

    [Fact]
    public void A_new_app_starts_unpublished_and_credits_its_creator()
    {
        var app = Create();

        app.IsPublished.ShouldBeFalse("an administrator finishes setting an entry up before anyone sees it");
        app.CreatedByUserId.ShouldBe(Creator);
        app.CreatedByDisplay.ShouldBe("admin@test");
        app.PublishedByUserId.ShouldBe(Creator);
        app.PublishedByDisplay.ShouldBe("admin@test");
        app.Icon.ShouldBeNull();
        app.Visibility.ShouldBe(CatalogVisibility.AllDevices);
        app.DisplayName.ShouldBe("Zoom");
        app.Description.ShouldBe("Video calls");
        app.Category.ShouldBe("Communication");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_display_name_is_required(string displayName) =>
        Should.Throw<ArgumentException>(() => Create(displayName: displayName));

    [Fact]
    public void A_null_display_name_is_rejected() =>
        Should.Throw<ArgumentException>(() => Create(displayName: null!));

    [Fact]
    public void A_display_name_is_at_most_128_characters()
    {
        Create(displayName: new string('n', 128)).DisplayName.Length.ShouldBe(128);

        Should.Throw<ArgumentException>(() => Create(displayName: new string('n', 129)));
    }

    [Fact]
    public void The_display_name_is_trimmed() =>
        Create(displayName: "  Zoom  ").DisplayName.ShouldBe("Zoom");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_description_or_category_is_stored_as_null(string? blank)
    {
        var app = Create(description: blank, category: blank);

        app.Description.ShouldBeNull();
        app.Category.ShouldBeNull();
    }

    [Fact]
    public void Description_and_category_are_trimmed_and_bounded()
    {
        Create(description: "  a line  ", category: "  Tools ").ShouldSatisfyAllConditions(
            app => app.Description.ShouldBe("a line"),
            app => app.Category.ShouldBe("Tools"));

        Should.Throw<ArgumentException>(() => Create(description: new string('d', 1001)));
        Should.Throw<ArgumentException>(() => Create(category: new string('c', 65)));
    }

    [Fact]
    public void Identities_must_not_be_empty()
    {
        Should.Throw<ArgumentException>(() => new CatalogApp(
            Guid.Empty, Guid.CreateVersion7(), "Zoom", null, null, CatalogVisibility.AllDevices, Creator, "admin"));
        Should.Throw<ArgumentException>(() => new CatalogApp(
            Guid.CreateVersion7(), Guid.Empty, "Zoom", null, null, CatalogVisibility.AllDevices, Creator, "admin"));
        Should.Throw<ArgumentException>(() => new CatalogApp(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "Zoom", null, null, CatalogVisibility.AllDevices, Guid.Empty, "admin"));
        Should.Throw<ArgumentException>(() => new CatalogApp(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "Zoom", null, null, CatalogVisibility.AllDevices, Creator, " "));
    }

    // ---------------------------------------------------------- publishing

    [Fact]
    public void Publish_records_who_approved_it()
    {
        var app = Create();
        var publisher = Guid.CreateVersion7();

        app.Publish(publisher, "approver@test");

        app.IsPublished.ShouldBeTrue();
        app.PublishedByUserId.ShouldBe(publisher);
        app.PublishedByDisplay.ShouldBe("approver@test");
        // The creator is history, not the approval.
        app.CreatedByUserId.ShouldBe(Creator);
    }

    [Fact]
    public void Unpublish_hides_the_app_but_keeps_the_last_publisher()
    {
        var app = Create();
        var publisher = Guid.CreateVersion7();
        app.Publish(publisher, "approver@test");

        app.Unpublish();

        app.IsPublished.ShouldBeFalse();
        app.PublishedByUserId.ShouldBe(publisher);
        app.PublishedByDisplay.ShouldBe("approver@test");
    }

    [Fact]
    public void Publish_needs_a_real_publisher()
    {
        var app = Create();

        Should.Throw<ArgumentException>(() => app.Publish(Guid.Empty, "approver@test"));
        Should.Throw<ArgumentException>(() => app.Publish(Guid.CreateVersion7(), " "));
        app.IsPublished.ShouldBeFalse();
    }

    [Fact]
    public void Describe_and_SetVisibility_change_the_presentation()
    {
        var app = Create();

        app.Describe("Zoom Workplace", "Meetings", "Collaboration");
        app.SetVisibility(CatalogVisibility.SelectedGroups);

        app.DisplayName.ShouldBe("Zoom Workplace");
        app.Description.ShouldBe("Meetings");
        app.Category.ShouldBe("Collaboration");
        app.Visibility.ShouldBe(CatalogVisibility.SelectedGroups);
    }

    // ---------------------------------------------------------------- icon

    [Fact]
    public void A_png_icon_is_accepted_as_is()
    {
        var app = Create();
        var png = Png();

        app.SetIcon(png);

        app.Icon.ShouldNotBeNull();
        app.Icon.ShouldBe(png);
    }

    [Fact]
    public void A_null_icon_clears_it()
    {
        var app = Create();
        app.SetIcon(Png());

        app.SetIcon(null);

        app.Icon.ShouldBeNull();
    }

    [Fact]
    public void An_empty_icon_is_rejected()
    {
        var app = Create();

        Should.Throw<ArgumentOutOfRangeException>(() => app.SetIcon([]));
        app.Icon.ShouldBeNull();
    }

    [Fact]
    public void An_oversized_icon_is_rejected()
    {
        var app = Create();

        // Exactly the limit is fine; one byte more is not.
        app.SetIcon(Png(CatalogApp.MaxIconBytes - PngSignature.Length));
        app.Icon!.Length.ShouldBe(CatalogApp.MaxIconBytes);

        Should.Throw<ArgumentOutOfRangeException>(() => app.SetIcon(Png(CatalogApp.MaxIconBytes - PngSignature.Length + 1)));
        app.Icon!.Length.ShouldBe(CatalogApp.MaxIconBytes, "a refused icon leaves the previous one in place");
    }

    [Fact]
    public void Anything_that_is_not_a_png_is_rejected()
    {
        var app = Create();

        // A JPEG, an SVG, and a PNG signature with its last byte wrong.
        Should.Throw<ArgumentException>(() => app.SetIcon([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46]));
        Should.Throw<ArgumentException>(() => app.SetIcon("<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray()));
        Should.Throw<ArgumentException>(() => app.SetIcon([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0B, 0x00]));

        // Fewer bytes than the signature cannot be a PNG either.
        Should.Throw<ArgumentException>(() => app.SetIcon([0x89, 0x50, 0x4E]));

        app.Icon.ShouldBeNull();
    }

    // ---------------------------------------------------------- visibility

    [Fact]
    public void An_unpublished_app_is_visible_to_nobody()
    {
        var group = Guid.CreateVersion7();

        Create(visibility: CatalogVisibility.AllDevices).IsVisibleTo(group, [group]).ShouldBeFalse();
        Create(visibility: CatalogVisibility.SelectedGroups).IsVisibleTo(group, [group]).ShouldBeFalse();
    }

    [Fact]
    public void A_published_all_devices_app_is_visible_whatever_the_group()
    {
        var app = Create(visibility: CatalogVisibility.AllDevices);
        app.Publish(Creator, "admin@test");

        app.IsVisibleTo(Guid.CreateVersion7(), []).ShouldBeTrue();
        app.IsVisibleTo(Guid.CreateVersion7(), [Guid.CreateVersion7()]).ShouldBeTrue();
    }

    [Fact]
    public void A_selected_groups_app_is_visible_only_to_a_listed_group()
    {
        var app = Create(visibility: CatalogVisibility.SelectedGroups);
        app.Publish(Creator, "admin@test");
        var sales = Guid.CreateVersion7();
        var engineering = Guid.CreateVersion7();

        app.IsVisibleTo(sales, [sales, engineering]).ShouldBeTrue();
        app.IsVisibleTo(engineering, [sales, engineering]).ShouldBeTrue();
        app.IsVisibleTo(Guid.CreateVersion7(), [sales, engineering]).ShouldBeFalse();
        app.IsVisibleTo(sales, []).ShouldBeFalse();
    }

    // ---------------------------------------------------------------- link

    [Fact]
    public void A_group_link_needs_both_ids()
    {
        var link = new CatalogAppGroup(Guid.CreateVersion7(), Guid.CreateVersion7());
        link.CatalogAppId.ShouldNotBe(Guid.Empty);
        link.DeviceGroupId.ShouldNotBe(Guid.Empty);

        Should.Throw<ArgumentException>(() => new CatalogAppGroup(Guid.Empty, Guid.CreateVersion7()));
        Should.Throw<ArgumentException>(() => new CatalogAppGroup(Guid.CreateVersion7(), Guid.Empty));
    }
}

/// <summary>
/// The record of one request from the portal. It outlives the app, so it
/// copies the name; the user name is informational and may be absent.
/// </summary>
public sealed class CatalogInstallRequestTests
{
    private static CatalogInstallRequest Create(
        string appName = "Zoom",
        string? requestedBy = @"CORP\jdoe",
        CatalogInstallOutcome outcome = CatalogInstallOutcome.Queued,
        Guid? taskId = null) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), appName, requestedBy, outcome, taskId);

    [Fact]
    public void A_valid_request_keeps_what_it_was_given()
    {
        var taskId = Guid.CreateVersion7();

        var request = Create(taskId: taskId);

        request.OrganizationId.ShouldNotBe(Guid.Empty);
        request.DeviceId.ShouldNotBe(Guid.Empty);
        request.CatalogAppId.ShouldNotBe(Guid.Empty);
        request.AppName.ShouldBe("Zoom");
        request.RequestedBy.ShouldBe(@"CORP\jdoe");
        request.Outcome.ShouldBe(CatalogInstallOutcome.Queued);
        request.TaskId.ShouldBe(taskId);
    }

    [Fact]
    public void A_refusal_has_no_task_and_may_have_no_user()
    {
        var request = Create(appName: "(not offered)", requestedBy: null, outcome: CatalogInstallOutcome.Refused);

        request.AppName.ShouldBe("(not offered)");
        request.RequestedBy.ShouldBeNull();
        request.Outcome.ShouldBe(CatalogInstallOutcome.Refused);
        request.TaskId.ShouldBeNull();
    }

    [Fact]
    public void Identities_must_not_be_empty()
    {
        Should.Throw<ArgumentException>(() => new CatalogInstallRequest(
            Guid.Empty, Guid.CreateVersion7(), Guid.CreateVersion7(), "Zoom", null, CatalogInstallOutcome.Queued, null));
        Should.Throw<ArgumentException>(() => new CatalogInstallRequest(
            Guid.CreateVersion7(), Guid.Empty, Guid.CreateVersion7(), "Zoom", null, CatalogInstallOutcome.Queued, null));
        Should.Throw<ArgumentException>(() => new CatalogInstallRequest(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, "Zoom", null, CatalogInstallOutcome.Queued, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void An_app_name_is_required(string appName) =>
        Should.Throw<ArgumentException>(() => Create(appName: appName));

    [Fact]
    public void An_app_name_is_at_most_128_characters()
    {
        Create(appName: new string('n', 128)).AppName.Length.ShouldBe(128);

        Should.Throw<ArgumentException>(() => Create(appName: new string('n', 129)));
    }

    [Fact]
    public void The_requesting_user_is_at_most_256_characters()
    {
        Create(requestedBy: new string('u', 256)).RequestedBy!.Length.ShouldBe(256);

        Should.Throw<ArgumentException>(() => Create(requestedBy: new string('u', 257)));
    }
}
