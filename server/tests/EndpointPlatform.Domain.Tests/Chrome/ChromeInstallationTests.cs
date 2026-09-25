using EndpointPlatform.Domain.Chrome;

namespace EndpointPlatform.Domain.Tests.Chrome;

/// <summary>
/// What the one-row-per-device Chrome installation may hold, and what "installed"
/// means.
/// </summary>
/// <remarks>
/// The row is upserted on every upload, so the test that carries weight is the one
/// proving a second <c>Apply</c> replaces every field including the ones the new
/// report leaves out. A stale executable path beside a fresh NotInstalled status
/// would describe an installation that no longer exists.
/// </remarks>
public sealed class ChromeInstallationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static ChromeInstallation Installed(ChromeInstallation? existing = null)
    {
        var installation = existing ?? new ChromeInstallation(Guid.CreateVersion7());

        installation.Apply(
            ChromeReportStatus.Available,
            version: "131.0.6778.86",
            executablePath: @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            architecture: "x64",
            channel: "stable",
            installationScope: "Machine",
            installedForUser: null,
            updaterVersion: "1.3.195.29",
            lastUpdateCheck: Now.AddHours(-3),
            collectedAt: Now);

        return installation;
    }

    [Fact]
    public void Apply_records_every_reported_field()
    {
        var installation = Installed();

        installation.Status.ShouldBe(ChromeReportStatus.Available);
        installation.Version.ShouldBe("131.0.6778.86");
        installation.ExecutablePath.ShouldBe(@"C:\Program Files\Google\Chrome\Application\chrome.exe");
        installation.Architecture.ShouldBe("x64");
        installation.Channel.ShouldBe("stable");
        installation.InstallationScope.ShouldBe("Machine");
        installation.InstalledForUser.ShouldBeNull();
        installation.UpdaterVersion.ShouldBe("1.3.195.29");
        installation.LastUpdateCheck.ShouldBe(Now.AddHours(-3));
        installation.CollectedAt.ShouldBe(Now);
    }

    /// <summary>
    /// The upload is a whole snapshot. After an uninstall the next report carries no
    /// installation, and every field the old one had must go with it.
    /// </summary>
    [Fact]
    public void A_second_apply_overwrites_every_field_including_the_ones_now_absent()
    {
        var installation = Installed();

        installation.Apply(
            ChromeReportStatus.NotInstalled,
            version: null, executablePath: null, architecture: null, channel: null,
            installationScope: null, installedForUser: null, updaterVersion: null,
            lastUpdateCheck: null, collectedAt: Now.AddDays(1));

        installation.Status.ShouldBe(ChromeReportStatus.NotInstalled);
        installation.Version.ShouldBeNull();
        installation.ExecutablePath.ShouldBeNull();
        installation.Architecture.ShouldBeNull();
        installation.Channel.ShouldBeNull();
        installation.InstallationScope.ShouldBeNull();
        installation.InstalledForUser.ShouldBeNull();
        installation.UpdaterVersion.ShouldBeNull();
        installation.LastUpdateCheck.ShouldBeNull();
        installation.CollectedAt.ShouldBe(Now.AddDays(1));
        installation.IsInstalled.ShouldBeFalse();
    }

    [Fact]
    public void A_per_user_install_records_whose_it_is()
    {
        var installation = new ChromeInstallation(Guid.CreateVersion7());

        installation.Apply(
            ChromeReportStatus.Available, "130.0.6723.117", null, "x64", "beta", "User",
            @"WORKGROUP\user", null, null, Now);

        installation.InstallationScope.ShouldBe("User");
        installation.InstalledForUser.ShouldBe(@"WORKGROUP\user");
        installation.IsInstalled.ShouldBeTrue();
    }

    // ---- what "installed" means --------------------------------------------

    [Fact]
    public void Available_with_a_version_is_installed()
    {
        Installed().IsInstalled.ShouldBeTrue();
    }

    /// <summary>
    /// A status the agent could not back with a version is not something the
    /// console can say anything about, so it does not count as an installation.
    /// </summary>
    [Fact]
    public void Available_without_a_version_is_not_installed()
    {
        var installation = new ChromeInstallation(Guid.CreateVersion7());

        installation.Apply(ChromeReportStatus.Available, null, null, null, null, null, null, null, null, Now);

        installation.IsInstalled.ShouldBeFalse();
    }

    [Theory]
    [InlineData(ChromeReportStatus.NotInstalled)]
    [InlineData(ChromeReportStatus.Error)]
    public void Any_status_but_available_is_not_installed_even_with_a_version(ChromeReportStatus status)
    {
        var installation = new ChromeInstallation(Guid.CreateVersion7());

        installation.Apply(status, "131.0.6778.86", null, null, null, null, null, null, null, Now);

        installation.IsInstalled.ShouldBeFalse(
            "a version left behind by an uninstall or read during a failed enumeration is not an installation");
    }

    [Fact]
    public void A_row_that_has_never_been_applied_is_not_installed()
    {
        new ChromeInstallation(Guid.CreateVersion7()).IsInstalled.ShouldBeFalse();
    }

    // ---- guards ------------------------------------------------------------

    [Fact]
    public void An_empty_device_id_is_refused()
    {
        Should.Throw<ArgumentException>(() => new ChromeInstallation(Guid.Empty));
    }

    /// <summary>
    /// The limits are the wire contract's <c>InventoryChromeInstallation.Max*</c>
    /// constants; the Agent API refuses anything longer, and the entity refuses it
    /// again so no other route can widen the column's contents.
    /// </summary>
    [Theory]
    [InlineData("version", 64)]
    [InlineData("executablePath", 512)]
    [InlineData("architecture", 16)]
    [InlineData("channel", 16)]
    [InlineData("installationScope", 16)]
    [InlineData("installedForUser", 256)]
    [InlineData("updaterVersion", 64)]
    public void A_value_over_the_contract_limit_is_refused_and_one_at_the_limit_is_kept(string field, int limit)
    {
        var atLimit = new string('a', limit);
        var overLimit = new string('a', limit + 1);

        Should.NotThrow(() => ApplyWith(field, atLimit));
        Should.Throw<ArgumentException>(() => ApplyWith(field, overLimit));
    }

    [Fact]
    public void Blank_optional_values_are_stored_as_unrecorded()
    {
        var installation = new ChromeInstallation(Guid.CreateVersion7());

        installation.Apply(
            ChromeReportStatus.Available, "  131.0.6778.86  ", "   ", "", null, null, null, null, null, Now);

        installation.Version.ShouldBe("131.0.6778.86");
        installation.ExecutablePath.ShouldBeNull("whitespace is not a path");
        installation.Architecture.ShouldBeNull("an empty string is not an architecture");
    }

    private static void ApplyWith(string field, string value)
    {
        var installation = new ChromeInstallation(Guid.CreateVersion7());

        installation.Apply(
            ChromeReportStatus.Available,
            version: field == "version" ? value : null,
            executablePath: field == "executablePath" ? value : null,
            architecture: field == "architecture" ? value : null,
            channel: field == "channel" ? value : null,
            installationScope: field == "installationScope" ? value : null,
            installedForUser: field == "installedForUser" ? value : null,
            updaterVersion: field == "updaterVersion" ? value : null,
            lastUpdateCheck: null,
            collectedAt: Now);
    }
}
