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
    private static readonly Uri Disable = new("/admin/v1/local-users/bulk/disable", UriKind.Relative);
    private static readonly Uri Enable = new("/admin/v1/local-users/bulk/enable", UriKind.Relative);
    private static readonly Uri Delete = new("/admin/v1/local-users/bulk/delete", UriKind.Relative);

    private sealed record BulkResponse(int Targeted, int Queued, int Rejected, int Offline, int Skipped);

    private sealed record LifecycleResponse(
        int Targeted, int Queued, int Rejected, int Offline, int SkippedNoAccount, int SkippedOther);

    private Task<HttpClient> SuperAdminAsync() =>
        _support.ClientAsAsync(AdminApiPostgresFixture.SuperAdminEmail);

    private static async Task<BulkResponse> OkAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<BulkResponse>())!;
    }

    private static async Task<LifecycleResponse> OkLifecycleAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<LifecycleResponse>())!;
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

    // --------------------------------------------- disable / enable / delete

    [Fact]
    public async Task Disabling_queues_for_devices_reporting_the_account_and_skips_the_rest()
    {
        using var client = await SuperAdminAsync();
        var hasAccount = await _support.SeedDeviceAsync();
        var missingAccount = await _support.SeedDeviceAsync();
        await ReportAccountAsync(hasAccount, "S-1-5-21-7-7-7-1060", "fieldops");

        var result = await OkLifecycleAsync(await client.PostAsJsonAsync(Disable, new
        {
            username = "fieldops",
            deviceIds = new[] { hasAccount, missingAccount },
        }));

        result.Targeted.ShouldBe(2);
        result.Queued.ShouldBe(1);
        result.SkippedNoAccount.ShouldBe(1);
        (await TaskCountAsync(hasAccount, DeviceTaskType.DisableLocalUser)).ShouldBe(1);
        (await TaskCountAsync(missingAccount, DeviceTaskType.DisableLocalUser)).ShouldBe(0);
    }

    [Fact]
    public async Task Enabling_matches_case_insensitively_and_queues_the_task_with_the_device_sid()
    {
        using var client = await SuperAdminAsync();
        var device = await _support.SeedDeviceAsync();
        await ReportAccountAsync(device, "S-1-5-21-7-7-7-1061", "KioskUser");

        var result = await OkLifecycleAsync(await client.PostAsJsonAsync(Enable, new
        {
            username = "kioskuser",
            deviceIds = new[] { device },
        }));

        result.Queued.ShouldBe(1);

        await using var db = _fixture.CreateDbContext();
        var task = await db.DeviceTasks.SingleAsync(
            t => t.DeviceId == device && t.Type == DeviceTaskType.EnableLocalUser);
        using var payload = System.Text.Json.JsonDocument.Parse(task.PayloadJson!);
        payload.RootElement.GetProperty("sid").GetString().ShouldBe("S-1-5-21-7-7-7-1061");
        payload.RootElement.GetProperty("enabled").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Deleting_for_a_group_queues_a_delete_task_on_each_member_reporting_the_account()
    {
        using var client = await SuperAdminAsync();
        var a = await _support.SeedDeviceAsync();
        var b = await _support.SeedDeviceAsync();
        var group = await _support.CreateGroupAsync(client, GroupTestSupport.UniqueName("Del"), a, b);
        await ReportAccountAsync(a, "S-1-5-21-7-7-7-1062", "tempcontractor");
        await ReportAccountAsync(b, "S-1-5-21-8-8-8-1062", "tempcontractor");

        var result = await OkLifecycleAsync(await client.PostAsJsonAsync(Delete, new
        {
            username = "tempcontractor",
            groupId = group,
        }));

        result.Targeted.ShouldBe(2);
        result.Queued.ShouldBe(2);
        (await TaskCountAsync(a, DeviceTaskType.DeleteLocalUser)).ShouldBe(1);
        (await TaskCountAsync(b, DeviceTaskType.DeleteLocalUser)).ShouldBe(1);
    }

    [Fact]
    public async Task Deleting_the_built_in_administrator_is_refused_without_queueing_anywhere()
    {
        using var client = await SuperAdminAsync();
        // On one device the name is the protected RID-500 account; on the other it is
        // an ordinary account. One protected match must refuse the WHOLE request:
        // nothing may be queued even where the name is harmless.
        var protectedDevice = await _support.SeedDeviceAsync();
        var ordinaryDevice = await _support.SeedDeviceAsync();
        await ReportAccountAsync(protectedDevice, "S-1-5-21-7-7-7-500", "corpadmin", administrator: true);
        await ReportAccountAsync(ordinaryDevice, "S-1-5-21-8-8-8-1063", "corpadmin");

        var response = await client.PostAsJsonAsync(Delete, new
        {
            username = "corpadmin",
            deviceIds = new[] { protectedDevice, ordinaryDevice },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await TaskCountAsync(protectedDevice, DeviceTaskType.DeleteLocalUser)).ShouldBe(0);
        (await TaskCountAsync(ordinaryDevice, DeviceTaskType.DeleteLocalUser)).ShouldBe(0);
    }

    [Fact]
    public async Task Disabling_the_built_in_administrator_is_refused()
    {
        using var client = await SuperAdminAsync();
        var device = await _support.SeedDeviceAsync();
        await ReportAccountAsync(device, "S-1-5-21-7-7-7-500", "breakglass", administrator: true);

        var response = await client.PostAsJsonAsync(Disable, new
        {
            username = "breakglass",
            deviceIds = new[] { device },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await TaskCountAsync(device, DeviceTaskType.DisableLocalUser)).ShouldBe(0);
    }

    [Fact]
    public async Task Enabling_the_built_in_administrator_is_refused_without_queueing_anywhere()
    {
        // Enable used to be deliberately exempt from the protected-account guard;
        // enable + reset composes into a silent RID-500 takeover, so the hands-off
        // rule is now symmetric and the whole request refuses up front.
        using var client = await SuperAdminAsync();
        var device = await _support.SeedDeviceAsync();
        await ReportAccountAsync(device, "S-1-5-21-7-7-7-500", "dormantroot", administrator: true);

        var response = await client.PostAsJsonAsync(Enable, new
        {
            username = "dormantroot",
            deviceIds = new[] { device },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("cannot be enabled");
        (await TaskCountAsync(device, DeviceTaskType.EnableLocalUser)).ShouldBe(0);
    }

    [Fact]
    public async Task Resetting_the_built_in_administrators_password_is_refused_without_queueing_anywhere()
    {
        // Even a caller holding user.change_type is refused: RID 500 is off limits in
        // every direction. One protected match refuses the WHOLE request, so the
        // device where the same name is ordinary gets nothing either.
        using var client = await SuperAdminAsync();
        var protectedDevice = await _support.SeedDeviceAsync();
        var ordinaryDevice = await _support.SeedDeviceAsync();
        await ReportAccountAsync(protectedDevice, "S-1-5-21-7-7-7-500", "rootalias", administrator: true);
        await ReportAccountAsync(ordinaryDevice, "S-1-5-21-8-8-8-1067", "rootalias");

        var response = await client.PostAsJsonAsync(Reset, new
        {
            username = "rootalias",
            password = "a-new-strong-password",
            deviceIds = new[] { protectedDevice, ordinaryDevice },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("protected");
        (await TaskCountAsync(protectedDevice, DeviceTaskType.ResetLocalUserPassword)).ShouldBe(0);
        (await TaskCountAsync(ordinaryDevice, DeviceTaskType.ResetLocalUserPassword)).ShouldBe(0);
    }

    [Fact]
    public async Task A_bulk_reset_naming_an_administrator_anywhere_requires_change_type()
    {
        // Resetting an administrator's password is the same grant as promoting one.
        // Helpdesk holds user.reset_password but not user.change_type, and one
        // admin-flagged match refuses the WHOLE request — a half-run against the
        // devices where the name is ordinary would still escalate machine by machine.
        using var client = await _support.ClientAsAsync(AdminApiPostgresFixture.HelpdeskEmail);
        var adminDevice = await _support.SeedDeviceAsync();
        var plainDevice = await _support.SeedDeviceAsync();
        await ReportAccountAsync(adminDevice, "S-1-5-21-7-7-7-1068", "deskadmin", administrator: true);
        await ReportAccountAsync(plainDevice, "S-1-5-21-8-8-8-1068", "deskadmin");

        var response = await client.PostAsJsonAsync(Reset, new
        {
            username = "deskadmin",
            password = "a-new-strong-password",
            deviceIds = new[] { adminDevice, plainDevice },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("user.change_type");
        (await TaskCountAsync(adminDevice, DeviceTaskType.ResetLocalUserPassword)).ShouldBe(0);
        (await TaskCountAsync(plainDevice, DeviceTaskType.ResetLocalUserPassword)).ShouldBe(0);
    }

    [Fact]
    public async Task Helpdesk_can_still_bulk_reset_a_standard_accounts_password()
    {
        using var client = await _support.ClientAsAsync(AdminApiPostgresFixture.HelpdeskEmail);
        var device = await _support.SeedDeviceAsync();
        await ReportAccountAsync(device, "S-1-5-21-7-7-7-1069", "frontcounter");

        var result = await OkAsync(await client.PostAsJsonAsync(Reset, new
        {
            username = "frontcounter",
            password = "a-new-strong-password",
            deviceIds = new[] { device },
        }));

        result.Queued.ShouldBe(1);
        (await TaskCountAsync(device, DeviceTaskType.ResetLocalUserPassword)).ShouldBe(1);
    }

    [Fact]
    public async Task An_operator_with_change_type_can_bulk_reset_an_ordinary_administrators_password()
    {
        using var client = await SuperAdminAsync();
        var device = await _support.SeedDeviceAsync();
        await ReportAccountAsync(device, "S-1-5-21-7-7-7-1070", "opsadmin", administrator: true);

        var result = await OkAsync(await client.PostAsJsonAsync(Reset, new
        {
            username = "opsadmin",
            password = "a-new-strong-password",
            deviceIds = new[] { device },
        }));

        result.Queued.ShouldBe(1);
        (await TaskCountAsync(device, DeviceTaskType.ResetLocalUserPassword)).ShouldBe(1);
    }

    [Fact]
    public async Task Disabling_the_last_enabled_administrator_is_rejected_per_device_not_queued()
    {
        using var client = await SuperAdminAsync();
        var device = await _support.SeedDeviceAsync();
        // The only enabled administrator this device has reported: the per-device
        // last-administrator guard must refuse it, surfacing as rejected, not queued.
        await ReportAccountAsync(device, "S-1-5-21-7-7-7-1064", "soleadmin", administrator: true);

        var result = await OkLifecycleAsync(await client.PostAsJsonAsync(Disable, new
        {
            username = "soleadmin",
            deviceIds = new[] { device },
        }));

        result.Targeted.ShouldBe(1);
        result.Queued.ShouldBe(0);
        result.Rejected.ShouldBe(1);
        (await TaskCountAsync(device, DeviceTaskType.DisableLocalUser)).ShouldBe(0);
    }

    [Fact]
    public async Task A_retired_device_in_a_delete_selection_is_skipped_not_queued()
    {
        using var client = await SuperAdminAsync();
        var live = await _support.SeedDeviceAsync();
        var retired = await _support.SeedDeviceAsync();
        await ReportAccountAsync(live, "S-1-5-21-7-7-7-1065", "leaver");
        await ReportAccountAsync(retired, "S-1-5-21-8-8-8-1065", "leaver");
        await using (var db = _fixture.CreateDbContext())
        {
            (await db.Devices.SingleAsync(d => d.Id == retired)).Retire();
            await db.SaveChangesAsync();
        }

        var result = await OkLifecycleAsync(await client.PostAsJsonAsync(Delete, new
        {
            username = "leaver",
            deviceIds = new[] { live, retired },
        }));

        result.Targeted.ShouldBe(2);
        result.Queued.ShouldBe(1);
        result.SkippedOther.ShouldBe(1);
        (await TaskCountAsync(retired, DeviceTaskType.DeleteLocalUser)).ShouldBe(0);
    }

    [Fact]
    public async Task A_lifecycle_request_with_no_target_is_refused()
    {
        using var client = await SuperAdminAsync();

        var response = await client.PostAsJsonAsync(Disable, new { username = "fieldops" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_lifecycle_request_without_a_username_is_refused()
    {
        using var client = await SuperAdminAsync();
        var device = await _support.SeedDeviceAsync();

        var response = await client.PostAsJsonAsync(Delete, new { deviceIds = new[] { device } });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Helpdesk_may_disable_but_not_delete()
    {
        using var client = await _support.ClientAsAsync(AdminApiPostgresFixture.HelpdeskEmail);
        var device = await _support.SeedDeviceAsync();
        await ReportAccountAsync(device, "S-1-5-21-7-7-7-1066", "frontdesk");

        // Disable/enable is the lock-an-account-out support action Helpdesk holds...
        var disabled = await OkLifecycleAsync(await client.PostAsJsonAsync(Disable, new
        {
            username = "frontdesk",
            deviceIds = new[] { device },
        }));
        disabled.Queued.ShouldBe(1);

        // ...deleting an account is not.
        var response = await client.PostAsJsonAsync(Delete, new
        {
            username = "frontdesk",
            deviceIds = new[] { device },
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
