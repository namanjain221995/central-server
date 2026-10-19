using System.Net;
using System.Net.Http.Json;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Tasks;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Fleet-wide local account management over real HTTP: who may do it, that one
/// account spec fans out to exactly the targeted devices as per-device typed tasks,
/// that a bulk reset resolves each device's SID from inventory and skips a machine
/// that never reported the account, and that the target shape is enforced.
/// </summary>
/// <remarks>
/// The fixture database is shared, so each test acts only on devices it created and
/// never asserts a global count — except by lower bound for the all-devices case.
/// CreateLocalUser and ResetLocalUserPassword have no minimum agent version, so no
/// device is skipped for being too old; the only skips here are retired devices and,
/// for a reset, devices that have not reported the account.
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class BulkLocalAccountEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;
    private readonly GroupTestSupport _support = new(fixture);

    private static readonly Uri Create = new("/admin/v1/local-users/bulk", UriKind.Relative);
    private static readonly Uri Reset = new("/admin/v1/local-users/bulk/reset-password", UriKind.Relative);

    private sealed record BulkResponse(int Targeted, int Queued, int Rejected, int Offline, int Skipped);

    private Task<HttpClient> SuperAdminAsync() =>
        _support.ClientAsAsync(AdminApiPostgresFixture.SuperAdminEmail);

    private static async Task<BulkResponse> OkAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<BulkResponse>())!;
    }

    private async Task ReportAccountAsync(Guid deviceId, string sid, string username, bool administrator = false)
    {
        await using var db = _fixture.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        db.DeviceLocalUsers.Add(new DeviceLocalUser(
            deviceId, sid, username, username, null, true, true, true, now, administrator, now));
        await db.SaveChangesAsync();
    }

    private async Task<int> TaskCountAsync(Guid deviceId, DeviceTaskType type)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.DeviceTasks.CountAsync(t => t.DeviceId == deviceId && t.Type == type);
    }

    // ------------------------------------------------------------------ create

    [Fact]
    public async Task Creating_for_a_group_queues_a_create_task_on_each_member()
    {
        using var client = await SuperAdminAsync();
        var a = await _support.SeedDeviceAsync();
        var b = await _support.SeedDeviceAsync();
        var group = await _support.CreateGroupAsync(client, GroupTestSupport.UniqueName("Acct"), a, b);

        var result = await OkAsync(await client.PostAsJsonAsync(Create, new
        {
            username = "svcdesk",
            password = "a-strong-password",
            accountType = "StandardUser",
            groupId = group,
        }));

        result.Targeted.ShouldBe(2);
        result.Queued.ShouldBe(2);
        (await TaskCountAsync(a, DeviceTaskType.CreateLocalUser)).ShouldBe(1);
        (await TaskCountAsync(b, DeviceTaskType.CreateLocalUser)).ShouldBe(1);
    }

    [Fact]
    public async Task Creating_for_all_devices_reaches_each_of_my_devices()
    {
        using var client = await SuperAdminAsync();
        var a = await _support.SeedDeviceAsync();
        var b = await _support.SeedDeviceAsync();

        var result = await OkAsync(await client.PostAsJsonAsync(Create, new
        {
            username = "fleetwide",
            password = "a-strong-password",
            accountType = "StandardUser",
            allDevices = true,
        }));

        // The database is shared, so there may be more; mine must be among them.
        result.Targeted.ShouldBeGreaterThanOrEqualTo(2);
        (await TaskCountAsync(a, DeviceTaskType.CreateLocalUser)).ShouldBe(1);
        (await TaskCountAsync(b, DeviceTaskType.CreateLocalUser)).ShouldBe(1);
    }

    [Fact]
    public async Task Creating_an_administrator_queues_the_task_with_the_admin_flag()
    {
        using var client = await SuperAdminAsync();
        var device = await _support.SeedDeviceAsync();

        var result = await OkAsync(await client.PostAsJsonAsync(Create, new
        {
            username = "breakglass",
            password = "a-strong-password",
            accountType = "Administrator",
            deviceIds = new[] { device },
        }));

        result.Queued.ShouldBe(1);

        await using var db = _fixture.CreateDbContext();
        var task = await db.DeviceTasks.SingleAsync(
            t => t.DeviceId == device && t.Type == DeviceTaskType.CreateLocalUser);
        using var payload = System.Text.Json.JsonDocument.Parse(task.PayloadJson!);
        payload.RootElement.GetProperty("administrator").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_retired_device_in_a_selection_is_skipped_not_queued()
    {
        using var client = await SuperAdminAsync();
        var live = await _support.SeedDeviceAsync();
        var retired = await _support.SeedDeviceAsync();
        await using (var db = _fixture.CreateDbContext())
        {
            (await db.Devices.SingleAsync(d => d.Id == retired)).Retire();
            await db.SaveChangesAsync();
        }

        var result = await OkAsync(await client.PostAsJsonAsync(Create, new
        {
            username = "svcdesk",
            password = "a-strong-password",
            deviceIds = new[] { live, retired },
        }));

        result.Targeted.ShouldBe(2);
        result.Queued.ShouldBe(1);
        result.Skipped.ShouldBe(1);
        (await TaskCountAsync(retired, DeviceTaskType.CreateLocalUser)).ShouldBe(0);
    }

    // --------------------------------------------------------------- reset path

    [Fact]
    public async Task Resetting_queues_for_devices_reporting_the_account_and_skips_the_rest()
    {
        using var client = await SuperAdminAsync();
        var hasAccount = await _support.SeedDeviceAsync();
        var missingAccount = await _support.SeedDeviceAsync();
        await ReportAccountAsync(hasAccount, "S-1-5-21-7-7-7-1050", "fieldsvc");

        var result = await OkAsync(await client.PostAsJsonAsync(Reset, new
        {
            username = "fieldsvc",
            password = "a-new-strong-password",
            deviceIds = new[] { hasAccount, missingAccount },
        }));

        result.Targeted.ShouldBe(2);
        result.Queued.ShouldBe(1);
        result.Skipped.ShouldBe(1);
        (await TaskCountAsync(hasAccount, DeviceTaskType.ResetLocalUserPassword)).ShouldBe(1);
        (await TaskCountAsync(missingAccount, DeviceTaskType.ResetLocalUserPassword)).ShouldBe(0);
    }

    [Fact]
    public async Task Reset_matches_the_reported_username_case_insensitively()
    {
        using var client = await SuperAdminAsync();
        var device = await _support.SeedDeviceAsync();
        await ReportAccountAsync(device, "S-1-5-21-7-7-7-1051", "FieldSvc");

        var result = await OkAsync(await client.PostAsJsonAsync(Reset, new
        {
            username = "fieldsvc",
            password = "a-new-strong-password",
            deviceIds = new[] { device },
        }));

        result.Queued.ShouldBe(1);
    }

    // ------------------------------------------------------------- shape + auth

    [Fact]
    public async Task A_request_with_no_target_is_refused()
    {
        using var client = await SuperAdminAsync();

        var response = await client.PostAsJsonAsync(Create, new
        {
            username = "svcdesk",
            password = "a-strong-password",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_request_with_two_targets_is_refused()
    {
        using var client = await SuperAdminAsync();
        var device = await _support.SeedDeviceAsync();

        var response = await client.PostAsJsonAsync(Create, new
        {
            username = "svcdesk",
            password = "a-strong-password",
            allDevices = true,
            deviceIds = new[] { device },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_short_password_is_refused_before_any_device_is_touched()
    {
        using var client = await SuperAdminAsync();
        var device = await _support.SeedDeviceAsync();

        var response = await client.PostAsJsonAsync(Create, new
        {
            username = "svcdesk",
            password = "short",
            deviceIds = new[] { device },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await TaskCountAsync(device, DeviceTaskType.CreateLocalUser)).ShouldBe(0);
    }

    [Fact]
    public async Task Helpdesk_may_not_create_accounts()
    {
        using var client = await _support.ClientAsAsync(AdminApiPostgresFixture.HelpdeskEmail);
        var device = await _support.SeedDeviceAsync();

        var response = await client.PostAsJsonAsync(Create, new
        {
            username = "svcdesk",
            password = "a-strong-password",
            deviceIds = new[] { device },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_reserved_username_is_refused()
    {
        using var client = await SuperAdminAsync();
        var device = await _support.SeedDeviceAsync();

        var response = await client.PostAsJsonAsync(Create, new
        {
            username = "administrator",
            password = "a-strong-password",
            deviceIds = new[] { device },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
