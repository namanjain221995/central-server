using System.Net;
using System.Net.Http.Json;
using EndpointPlatform.Contracts;
using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Peripherals;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.AgentApi.Tests;

/// <summary>
/// A phone reported by an agent: stored as a portable device, audited on first
/// sight, restricted until an administrator says otherwise, and recorded with
/// how far the agent got in enforcing that.
/// </summary>
[Collection(AgentApiPostgresCollection.Name)]
public sealed class UsbPortableDeviceReportTests(AgentApiPostgresFixture fixture)
{
    private readonly AgentApiPostgresFixture _fixture = fixture;

    private const string PhoneId = @"USB\VID_2717&PID_FF40\EXAMPLE0SERIAL01";

    private async Task<(Guid DeviceId, string Credential, Guid OrgId)> EnrollAsync(string hostname)
    {
        await using var db = _fixture.CreateDbContext();
        var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
        var secret = SecretGenerator.GenerateSecret();

        db.EnrollmentTokens.Add(new EnrollmentToken(
            org.Id, $"tk-{Guid.CreateVersion7():N}", SecretGenerator.HashSecret(secret),
            Guid.CreateVersion7(), "admin@test", DateTimeOffset.UtcNow.AddHours(1), 1));
        await db.SaveChangesAsync();

        using var client = _fixture.Factory.CreateClient();
        var response = await client.SendAsync(Request(AgentProtocol.Routes.Enroll, new EnrollRequest(
            secret, hostname, $"machine-{Guid.CreateVersion7():N}", "1.16.0", null)));

        response.EnsureSuccessStatusCode();
        var body = (await response.Content.ReadFromJsonAsync<EnrollResponse>())!;

        return (body.DeviceId, $"{body.CredentialKeyId}.{body.CredentialSecret}", org.Id);
    }

    private static HttpRequestMessage Request(string route, object body, string? credential = null)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Post, new Uri(AgentProtocol.RoutePrefix + route, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };

        message.Headers.Add(AgentProtocol.Headers.ProtocolVersion, AgentProtocol.Version.ToString());

        if (credential is not null)
        {
            message.Headers.Add(AgentProtocol.Headers.Credential, credential);
        }

        return message;
    }

    private static UsbDeviceReport Phone(
        string? enforced = "Restricted", string? error = null, string? status = "Verified", string deviceClass = "PortableDevice") =>
        new(PhoneId, deviceClass, "2717", "FF40", "EXAMPLE0SERIAL01", "Xiaomi", "Redmi Note 14 Pro 5G",
            @"USB\VID_2717&PID_FF40", IsConnected: true, enforced, error, status);

    private async Task<UsbPolicyResponse> ReportAsync(string credential, params UsbDeviceReport[] devices)
    {
        using var client = _fixture.Factory.CreateClient();

        var response = await client.SendAsync(Request(
            AgentProtocol.Routes.Usb, new UsbReport(devices, DateTimeOffset.UtcNow), credential));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<UsbPolicyResponse>())!;
    }

    [Fact]
    public async Task A_phone_is_stored_as_a_portable_device_restricted_and_audited()
    {
        var (deviceId, credential, _) = await EnrollAsync("USB-PHONE-1");

        var policy = await ReportAsync(credential, Phone());
        policy.Grants.ShouldBeEmpty();

        await using var db = _fixture.CreateDbContext();
        var usb = await db.UsbDevices.AsNoTracking().SingleAsync(u => u.DeviceId == deviceId);

        usb.DeviceClass.ShouldBe(UsbDeviceClass.PortableDevice);
        usb.IsRestrictable.ShouldBeTrue();
        usb.SupportsReadOnly.ShouldBeFalse();
        usb.Policy.ShouldBe(UsbStoragePolicy.Restricted);
        usb.EnforcedPolicy.ShouldBe(UsbStoragePolicy.Restricted);
        usb.EnforcementStatus.ShouldBe(UsbEnforcementStatus.Verified);
        usb.IsEnforcementVerified.ShouldBeTrue();

        (await db.AuditLogEntries.AsNoTracking()
            .AnyAsync(a => a.Action == "usb.portable_device.connected" && a.DeviceId == deviceId))
            .ShouldBeTrue("first sight of a phone on a managed endpoint is an audit event");
    }

    /// <summary>
    /// The status is recorded exactly as reported and never promoted: a value
    /// the server does not know, or none at all (an older agent), is null.
    /// </summary>
    [Theory]
    [InlineData("Verified", UsbEnforcementStatus.Verified)]
    [InlineData("verified", UsbEnforcementStatus.Verified)]
    [InlineData("Unverified", UsbEnforcementStatus.Unverified)]
    [InlineData("RequiresRestart", UsbEnforcementStatus.RequiresRestart)]
    [InlineData("Failed", UsbEnforcementStatus.Failed)]
    [InlineData("1", null)]
    [InlineData("Confirmed", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public async Task The_enforcement_status_is_recorded_as_reported(string? wire, UsbEnforcementStatus? expected)
    {
        var (deviceId, credential, _) = await EnrollAsync($"USB-PHONE-STATUS-{Guid.CreateVersion7():N}"[..30]);

        await ReportAsync(credential, Phone(status: wire));

        await using var db = _fixture.CreateDbContext();
        var usb = await db.UsbDevices.AsNoTracking().SingleAsync(u => u.DeviceId == deviceId);

        usb.EnforcementStatus.ShouldBe(expected);
    }

    [Fact]
    public async Task A_restart_deferred_restriction_is_recorded_as_not_enforced()
    {
        var (deviceId, credential, _) = await EnrollAsync("USB-PHONE-RESTART");

        await ReportAsync(credential, Phone(enforced: null, error: "a program is holding the device open", status: "RequiresRestart"));

        await using var db = _fixture.CreateDbContext();
        var usb = await db.UsbDevices.AsNoTracking().SingleAsync(u => u.DeviceId == deviceId);

        usb.EnforcedPolicy.ShouldBeNull();
        usb.EnforcementStatus.ShouldBe(UsbEnforcementStatus.RequiresRestart);
        usb.HasEnforcementFailure.ShouldBeTrue();
        usb.IsPolicyEnforced.ShouldBeFalse();
    }

    /// <summary>
    /// A class sent as a number is not a class. The enum's ordinal must never be
    /// a way to reach a restrictable class — or any class — by accident.
    /// </summary>
    [Theory]
    [InlineData("7")]
    [InlineData("1")]
    [InlineData("Phone")]
    public async Task A_class_that_is_not_a_name_is_stored_as_unknown(string deviceClass)
    {
        var (deviceId, credential, _) = await EnrollAsync($"USB-PHONE-CLASS-{Guid.CreateVersion7():N}"[..30]);

        await ReportAsync(credential, Phone(deviceClass: deviceClass));

        await using var db = _fixture.CreateDbContext();
        var usb = await db.UsbDevices.AsNoTracking().SingleAsync(u => u.DeviceId == deviceId);

        usb.DeviceClass.ShouldBe(UsbDeviceClass.Unknown);
        usb.IsRestrictable.ShouldBeFalse();
    }

    [Fact]
    public async Task A_read_write_grant_on_a_phone_is_published_to_the_endpoint()
    {
        var (deviceId, credential, orgId) = await EnrollAsync("USB-PHONE-GRANT");
        await ReportAsync(credential, Phone());

        await using (var db = _fixture.CreateDbContext())
        {
            var usb = await db.UsbDevices.SingleAsync(u => u.DeviceId == deviceId);
            usb.Grant(UsbStoragePolicy.Enabled, DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow);

            db.UsbAccessRequests.Add(UsbAccessRequest.GrantByAdministrator(
                orgId, deviceId, usb.Id, usb.InstanceId, UsbStoragePolicy.Enabled, "Field photos.",
                Guid.CreateVersion7(), "admin@test", TimeSpan.FromHours(1), DateTimeOffset.UtcNow));

            await db.SaveChangesAsync();
        }

        var policy = await ReportAsync(credential, Phone(enforced: "Enabled"));

        policy.Grants.Count.ShouldBe(1);
        policy.Grants[0].InstanceId.ShouldBe(PhoneId);
        policy.Grants[0].Policy.ShouldBe("Enabled");
    }

    /// <summary>
    /// An agent cannot talk a phone open by claiming to enforce read/write on it.
    /// </summary>
    [Fact]
    public async Task A_phone_reported_as_enabled_does_not_become_enabled()
    {
        var (deviceId, credential, _) = await EnrollAsync("USB-PHONE-LIAR");

        var policy = await ReportAsync(credential, Phone(enforced: "Enabled"));

        policy.Grants.ShouldBeEmpty();

        await using var db = _fixture.CreateDbContext();
        var usb = await db.UsbDevices.AsNoTracking().SingleAsync(u => u.DeviceId == deviceId);

        usb.Policy.ShouldBe(UsbStoragePolicy.Restricted);
        usb.EnforcedPolicy.ShouldBe(UsbStoragePolicy.Enabled);
        usb.IsPolicyEnforced.ShouldBeFalse("the report drifts from the decision; it does not change it");
    }
}
