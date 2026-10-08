using EndpointPlatform.Domain.Devices;

namespace EndpointPlatform.Domain.Tests.Devices;

public sealed class DeviceTests
{
    private static readonly Guid OrganizationId = Guid.CreateVersion7();
    private static readonly Guid TokenId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    private static Device EnrollDevice() =>
        Device.Enroll(OrganizationId, "PC-023", "smbios-uuid-1", "0.1.0", "Windows 11 Pro", TokenId, Now);

    [Fact]
    public void Enrollment_creates_an_active_device_with_last_seen_set()
    {
        var device = EnrollDevice();

        device.Status.ShouldBe(DeviceStatus.Active);
        device.LastSeenAt.ShouldBe(Now);
        device.EnrolledAt.ShouldBe(Now);
        device.EnrolledWithTokenId.ShouldBe(TokenId);
    }

    [Fact]
    public void Heartbeat_updates_facts_and_last_seen()
    {
        var device = EnrollDevice();

        device.RecordHeartbeat("PC-023-RENAMED", "0.2.0", "Windows 11 Pro 26H2", Now.AddMinutes(5));

        device.Hostname.ShouldBe("PC-023-RENAMED");
        device.AgentVersion.ShouldBe("0.2.0");
        device.OperatingSystem.ShouldBe("Windows 11 Pro 26H2");
        device.LastSeenAt.ShouldBe(Now.AddMinutes(5));
    }

    [Fact]
    public void Heartbeat_without_os_keeps_the_previous_value()
    {
        var device = EnrollDevice();

        device.RecordHeartbeat("PC-023", "0.1.0", operatingSystem: null, Now.AddMinutes(1));

        device.OperatingSystem.ShouldBe("Windows 11 Pro");
    }

    [Fact]
    public void A_retired_device_rejects_heartbeats()
    {
        var device = EnrollDevice();
        device.Retire();

        Should.Throw<InvalidOperationException>(() =>
            device.RecordHeartbeat("PC-023", "0.1.0", null, Now.AddMinutes(1)));
    }

    [Fact]
    public void A_retired_device_rejects_re_enrollment()
    {
        // Retirement revokes trust. Silently re-admitting the machine on the next
        // enrollment attempt would defeat the point of retiring it.
        var device = EnrollDevice();
        device.Retire();

        Should.Throw<InvalidOperationException>(() =>
            device.ReEnroll("PC-023", "0.3.0", null, Guid.CreateVersion7(), Now.AddDays(1)));
    }

    [Fact]
    public void Re_enrollment_refreshes_facts_but_keeps_identity()
    {
        var device = EnrollDevice();
        var originalId = device.Id;
        var newToken = Guid.CreateVersion7();

        device.ReEnroll("PC-023-REBUILT", "0.5.0", "Windows 11 Enterprise", newToken, Now.AddDays(30));

        device.Id.ShouldBe(originalId, "re-enrollment must preserve device identity and history");
        device.Hostname.ShouldBe("PC-023-REBUILT");
        device.EnrolledWithTokenId.ShouldBe(newToken);
        device.EnrolledAt.ShouldBe(Now.AddDays(30));
    }

    [Fact]
    public void Online_is_derived_from_heartbeat_staleness()
    {
        var device = EnrollDevice();
        var staleAfter = TimeSpan.FromMinutes(3);

        device.IsOnline(Now.AddMinutes(2), staleAfter).ShouldBeTrue();
        device.IsOnline(Now.AddMinutes(3), staleAfter).ShouldBeTrue("boundary is inclusive");
        device.IsOnline(Now.AddMinutes(4), staleAfter).ShouldBeFalse();
    }

    [Fact]
    public void A_retired_device_is_never_online()
    {
        var device = EnrollDevice();
        device.Retire();

        device.IsOnline(Now, TimeSpan.FromDays(365)).ShouldBeFalse();
    }

    [Fact]
    public void A_heartbeat_with_uptime_records_the_boot_on_the_servers_clock_and_one_without_keeps_the_last()
    {
        var device = Device.Enroll(Guid.CreateVersion7(), "PC-BOOT", "m-boot", "1.15.0", null, Guid.CreateVersion7(), Now);
        device.LastBootAt.ShouldBeNull();

        device.RecordHeartbeat("PC-BOOT", "1.15.0", null, Now.AddMinutes(10), TimeSpan.FromMinutes(3));
        device.LastBootAt.ShouldBe(Now.AddMinutes(7));

        device.RecordHeartbeat("PC-BOOT", "1.14.0", null, Now.AddMinutes(11));
        device.LastBootAt.ShouldBe(Now.AddMinutes(7), "an older agent says nothing; what is known stands");

        device.RecordHeartbeat("PC-BOOT", "1.15.0", null, Now.AddMinutes(12), TimeSpan.FromDays(-1));
        device.LastBootAt.ShouldBe(Now.AddMinutes(7), "a nonsense uptime is ignored");
    }
}
