using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Domain.Tasks;
using Microsoft.EntityFrameworkCore;
using static EndpointPlatform.Api.Tests.GroupTestSupport;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Screen locks over real HTTP: who may lock, that a device is never locked without
/// a recovery code, which machines actually get the task, that the payload carries
/// a verifier and never the code, releasing again, and the derived "locked" the
/// device list reports.
/// </summary>
/// <remarks>
/// Delivery rides the existing task channel, so most assertions are about the
/// <c>device_tasks</c> rows, exactly as the announcement tests are. The fixture
/// database is shared, so each test acts only on devices and locks it created, and
/// every test that locks sets the unlock code first (idempotent) so order cannot
/// matter -- except the refusal test, which uses an organization of its own that
/// never gets a code.
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class ScreenLockEndpointTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;
    private readonly GroupTestSupport _support = new(fixture);

    private static readonly Uri Locks = new("/admin/v1/screen-locks", UriKind.Relative);
    private static readonly Uri Release = new("/admin/v1/screen-locks/release", UriKind.Relative);
    private static readonly Uri UnlockCode = new("/admin/v1/settings/screen-unlock-code", UriKind.Relative);

    /// <summary>The oldest agent whose catalogue entry admits EngageScreenLock.</summary>
    private const string CurrentAgent = "1.19.0";

    private const string TheCode = "Recovery-Code-123!";

    private sealed record EngageResponse(Guid LockId, int Targeted, int Queued, int Skipped, int Offline);

    private Task<HttpClient> SuperAdminAsync() => _support.ClientAsAsync(AdminApiPostgresFixture.SuperAdminEmail);

    private static StringContent Body(object body) => Json(body);

    private async Task SetCodeAsync(HttpClient client, string code = TheCode)
    {
        var response = await client.PutAsync(UnlockCode, Body(new { code }));
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private async Task<EngageResponse> EngagedAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<EngageResponse>())!;
    }

    private async Task RetireAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        (await db.Devices.SingleAsync(d => d.Id == deviceId)).Retire();
        await db.SaveChangesAsync();
    }

    private async Task<string?> SkipReasonAsync(Guid lockId, Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.ScreenLockDevices
            .Where(r => r.ScreenLockId == lockId && r.DeviceId == deviceId)
            .Select(r => r.SkipReason)
            .SingleAsync();
    }

    /// <summary>Delivers the device's engage task, so the lock counts as "reached the screen".</summary>
    private async Task DeliverEngageAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        var task = await db.DeviceTasks
            .Where(t => t.DeviceId == deviceId && t.Type == DeviceTaskType.EngageScreenLock)
            .OrderByDescending(t => t.CreatedAt)
            .FirstAsync();
        task.TryDeliver(DateTimeOffset.UtcNow).ShouldBeTrue();
        await db.SaveChangesAsync();
    }

    /// <summary>Delivers then fails the device's engage task, as a device that could not lock would.</summary>
    private async Task FailEngageAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        var task = await db.DeviceTasks
            .Where(t => t.DeviceId == deviceId && t.Type == DeviceTaskType.EngageScreenLock)
            .OrderByDescending(t => t.CreatedAt)
            .FirstAsync();
        var now = DateTimeOffset.UtcNow;
        task.TryDeliver(now).ShouldBeTrue();
        task.TryComplete(succeeded: false, "could not lock", null, now).ShouldBeTrue();
        await db.SaveChangesAsync();
    }

    /// <summary>Simulates a heartbeat in which the agent reports its current lock state.</summary>
    private async Task ReportScreenLockedAsync(Guid deviceId, bool? value)
    {
        await using var db = _fixture.CreateDbContext();
        await db.Devices.Where(d => d.Id == deviceId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ScreenLockedReported, value));
    }

    // ------------------------------------------------------------------ groups

    [Fact]
    public async Task Locking_a_group_reaches_current_agents_and_records_why_old_ones_were_skipped()
    {
        using var client = await SuperAdminAsync();
        await SetCodeAsync(client);
        var current = await _support.SeedDeviceAsync(agentVersion: CurrentAgent);
        var newer = await _support.SeedDeviceAsync(agentVersion: "1.19.4");
        var old = await _support.SeedDeviceAsync(agentVersion: "1.18.0");
        var group = await _support.CreateGroupAsync(client, UniqueName("Lock"), current, newer, old);

        var result = await EngagedAsync(await client.PostAsync(Locks, Body(new
        {
            message = "This device is locked by IT.",
            contactLine = "Call the service desk on x1234.",
            groupId = group,
        })));

        result.Targeted.ShouldBe(3);
        result.Queued.ShouldBe(2);
        result.Offline.ShouldBe(0, "every targeted device was seeded with a fresh heartbeat");
        result.Skipped.ShouldBe(1);

        (await _support.TasksAsync(current, DeviceTaskType.EngageScreenLock)).ShouldBe(1);
        (await _support.TasksAsync(newer, DeviceTaskType.EngageScreenLock)).ShouldBe(1);
        (await _support.TasksAsync(old, DeviceTaskType.EngageScreenLock)).ShouldBe(0,
            "an agent without the lock executor must never be queued the task");

        (await SkipReasonAsync(result.LockId, old)).ShouldBe("AgentTooOld");
        (await SkipReasonAsync(result.LockId, current)).ShouldBeNull();
    }

    [Fact]
    public async Task A_retired_device_is_recorded_as_skipped_and_gets_no_task()
    {
        using var client = await SuperAdminAsync();
        await SetCodeAsync(client);
        var active = await _support.SeedDeviceAsync(agentVersion: CurrentAgent);
        var retired = await _support.SeedDeviceAsync(agentVersion: CurrentAgent);
        await RetireAsync(retired);

        var result = await EngagedAsync(await client.PostAsync(Locks, Body(new
        {
            message = "Only live machines lock.",
            deviceIds = new[] { active, retired },
        })));

        result.Targeted.ShouldBe(2);
        result.Queued.ShouldBe(1);
        result.Skipped.ShouldBe(1);
        (await _support.TasksAsync(retired, DeviceTaskType.EngageScreenLock)).ShouldBe(0);
        (await SkipReasonAsync(result.LockId, retired)).ShouldBe("Retired");
    }

    // ----------------------------------------------------------- unlock code

    [Fact]
    public async Task Locking_without_an_unlock_code_is_refused_and_writes_nothing()
    {
        // An organization of its own, so the shared default org's code cannot leak in.
        var (client, deviceId) = await SeedIsolatedOrgAdminAndDeviceAsync();
        using (client)
        {
            (await client.GetAsync(UnlockCode)).Content.ReadFromJsonAsync<JsonElement>().Result
                .GetProperty("isSet").GetBoolean().ShouldBeFalse();

            var response = await client.PostAsync(Locks, Body(new
            {
                message = "Lock me.",
                deviceIds = new[] { deviceId },
            }));

            response.StatusCode.ShouldBe(HttpStatusCode.Conflict,
                "a device must never be locked when there is no code that could unlock it on the machine");

            (await _support.TasksAsync(deviceId, DeviceTaskType.EngageScreenLock)).ShouldBe(0,
                "a refused lock must queue nothing");
            await using var db = _fixture.CreateDbContext();
            (await db.ScreenLockDevices.CountAsync(r => r.DeviceId == deviceId)).ShouldBe(0);
        }
    }

    [Fact]
    public async Task Setting_the_unlock_code_stores_only_a_verifier_and_never_returns_the_code()
    {
        using var client = await SuperAdminAsync();
        await SetCodeAsync(client);

        var status = await (await client.GetAsync(UnlockCode)).Content.ReadFromJsonAsync<JsonElement>();
        status.GetProperty("isSet").GetBoolean().ShouldBeTrue();
        status.TryGetProperty("updatedAt", out var updatedAt).ShouldBeTrue();
        updatedAt.ValueKind.ShouldNotBe(JsonValueKind.Null);
        status.EnumerateObject().Select(p => p.Name)
            .ShouldBe(["isSet", "updatedAt"], ignoreOrder: true, "the status must never carry the code");

        var orgId = await OrgOfAsync(AdminApiPostgresFixture.SuperAdminEmail);
        await using var db = _fixture.CreateDbContext();
        var setting = await db.OrganizationSecuritySettings.SingleAsync(s => s.OrganizationId == orgId);
        setting.ScreenUnlockAlgo.ShouldBe("pbkdf2-sha256");
        setting.ScreenUnlockIterations.ShouldBeGreaterThanOrEqualTo(100_000);
        setting.ScreenUnlockSalt.ShouldNotBeNullOrEmpty();
        setting.ScreenUnlockHash.ShouldNotBeNullOrEmpty();
        setting.ScreenUnlockSalt.ShouldNotContain(TheCode);
        setting.ScreenUnlockHash.ShouldNotContain(TheCode);

        var audit = await db.AuditLogEntries
            .Where(a => a.Action == "security.screen_unlock_code.set" && a.TargetId == orgId.ToString())
            .OrderByDescending(a => a.OccurredAt)
            .FirstOrDefaultAsync();
        audit.ShouldNotBeNull();
        audit.RequiredPermission.ShouldBe(Permissions.Device.ScreenLock);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    public async Task A_too_short_unlock_code_is_refused(string code)
    {
        using var client = await SuperAdminAsync();
        (await client.PutAsync(UnlockCode, Body(new { code }))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ---------------------------------------------------------- the payload

    /// <summary>
    /// The task is the wire contract: camelCase keys the agent reads, the VERIFIER
    /// of the code rather than the code itself, and a week of lifetime so an offline
    /// machine still comes up locked on reconnect.
    /// </summary>
    [Fact]
    public async Task The_queued_task_carries_the_verifier_not_the_code_and_lives_a_week()
    {
        using var client = await SuperAdminAsync();
        await SetCodeAsync(client);
        var device = await _support.SeedDeviceAsync(agentVersion: CurrentAgent);
        var before = DateTimeOffset.UtcNow;

        var result = await EngagedAsync(await client.PostAsync(Locks, Body(new
        {
            message = "This device is locked by IT.",
            contactLine = "Call x1234.",
            deviceIds = new[] { device },
        })));

        await using var db = _fixture.CreateDbContext();
        var task = await db.DeviceTasks.SingleAsync(
            t => t.DeviceId == device && t.Type == DeviceTaskType.EngageScreenLock);

        using var payload = JsonDocument.Parse(task.PayloadJson!);
        payload.RootElement.EnumerateObject().Select(p => p.Name)
            .ShouldBe(["lockId", "message", "contactLine", "unlock"], ignoreOrder: true);
        payload.RootElement.GetProperty("lockId").GetGuid().ShouldBe(result.LockId);
        payload.RootElement.GetProperty("message").GetString().ShouldBe("This device is locked by IT.");

        var unlock = payload.RootElement.GetProperty("unlock");
        unlock.GetProperty("algo").GetString().ShouldBe("pbkdf2-sha256");
        unlock.GetProperty("iterations").GetInt32().ShouldBeGreaterThanOrEqualTo(100_000);
        unlock.GetProperty("salt").GetString().ShouldNotBeNullOrEmpty();
        unlock.GetProperty("hash").GetString().ShouldNotBeNullOrEmpty();

        task.PayloadJson!.Contains(TheCode, StringComparison.Ordinal).ShouldBeFalse(
            "the plaintext unlock code must never reach a persisted task payload");

        task.ExpiresAt.ShouldBeGreaterThan(before.AddDays(6));
        task.ExpiresAt.ShouldBeLessThan(DateTimeOffset.UtcNow.AddDays(8));

        var audit = await db.AuditLogEntries
            .SingleOrDefaultAsync(a => a.Action == "device.screen_lock.engage"
                                       && a.TargetId == result.LockId.ToString());
        audit.ShouldNotBeNull("locking screens must leave its own audit entry");
        audit.RequiredPermission.ShouldBe(Permissions.Device.ScreenLock);
        using var state = JsonDocument.Parse(audit.NewState!);
        state.RootElement.GetProperty("targeted").GetInt32().ShouldBe(1);
        state.RootElement.GetProperty("queued").GetInt32().ShouldBe(1);
    }

    /// <summary>
    /// The longest allowed message locks cleanly: it is 500 characters but the
    /// audit target display is bounded at 256, so a naive audit would throw here and
    /// take the whole engage down. The full text still rides in the task payload.
    /// </summary>
    [Fact]
    public async Task The_longest_allowed_message_locks_without_overflowing_the_audit()
    {
        using var client = await SuperAdminAsync();
        await SetCodeAsync(client);
        var device = await _support.SeedDeviceAsync(agentVersion: CurrentAgent);
        var message = new string('m', 500);

        var result = await EngagedAsync(await client.PostAsync(Locks, Body(new
        {
            message,
            deviceIds = new[] { device },
        })));

        result.Queued.ShouldBe(1);

        await using var db = _fixture.CreateDbContext();
        var task = await db.DeviceTasks.SingleAsync(
            t => t.DeviceId == device && t.Type == DeviceTaskType.EngageScreenLock);
        JsonDocument.Parse(task.PayloadJson!).RootElement.GetProperty("message").GetString()!.Length.ShouldBe(500);
        (await db.AuditLogEntries.CountAsync(a => a.Action == "device.screen_lock.engage"
            && a.TargetId == result.LockId.ToString())).ShouldBe(1);
    }

    [Fact]
    public async Task An_offline_device_is_queued_but_counted_as_offline()
    {
        using var client = await SuperAdminAsync();
        await SetCodeAsync(client);
        var online = await _support.SeedDeviceAsync(agentVersion: CurrentAgent);
        var stale = await _support.SeedDeviceAsync(online: false, agentVersion: CurrentAgent);

        var result = await EngagedAsync(await client.PostAsync(Locks, Body(new
        {
            message = "You will be locked on reconnect.",
            deviceIds = new[] { online, stale },
        })));

        result.Queued.ShouldBe(2, "offline is not skipped: the task waits for the device");
        result.Offline.ShouldBe(1);
        result.Skipped.ShouldBe(0);
        (await _support.TasksAsync(stale, DeviceTaskType.EngageScreenLock)).ShouldBe(1);
    }

    // ------------------------------------------------------------- releasing

    [Fact]
    public async Task Releasing_by_lock_id_queues_an_unlock_for_each_locked_device()
    {
        using var client = await SuperAdminAsync();
        await SetCodeAsync(client);
        var a = await _support.SeedDeviceAsync(agentVersion: CurrentAgent);
        var b = await _support.SeedDeviceAsync(agentVersion: CurrentAgent);

        var locked = await EngagedAsync(await client.PostAsync(Locks, Body(new
        {
            message = "Locked.",
            deviceIds = new[] { a, b },
        })));

        var release = await client.PostAsync(Release, Body(new { lockId = locked.LockId }));
        release.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await release.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("released").GetInt32().ShouldBe(2);

        (await _support.TasksAsync(a, DeviceTaskType.ReleaseScreenLock)).ShouldBe(1);
        (await _support.TasksAsync(b, DeviceTaskType.ReleaseScreenLock)).ShouldBe(1);

        await using var db = _fixture.CreateDbContext();
        (await db.ScreenLockDevices.CountAsync(r => r.ScreenLockId == locked.LockId && r.ReleaseTaskId != null))
            .ShouldBe(2, "each released row records the task that lifted it");

        // Releasing again lifts nothing: the rows are already released.
        var again = await client.PostAsync(Release, Body(new { lockId = locked.LockId }));
        (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("released").GetInt32().ShouldBe(0);

        var audit = await db.AuditLogEntries.CountAsync(x => x.Action == "device.screen_lock.release");
        audit.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Releasing_by_device_queues_an_unlock_only_for_the_named_device()
    {
        using var client = await SuperAdminAsync();
        await SetCodeAsync(client);
        var a = await _support.SeedDeviceAsync(agentVersion: CurrentAgent);
        var b = await _support.SeedDeviceAsync(agentVersion: CurrentAgent);

        await EngagedAsync(await client.PostAsync(Locks, Body(new { message = "Locked.", deviceIds = new[] { a, b } })));

        var release = await client.PostAsync(Release, Body(new { deviceIds = new[] { a } }));
        (await release.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("released").GetInt32().ShouldBe(1);

        (await _support.TasksAsync(a, DeviceTaskType.ReleaseScreenLock)).ShouldBe(1);
        (await _support.TasksAsync(b, DeviceTaskType.ReleaseScreenLock)).ShouldBe(0,
            "the device that was not named stays locked");
    }

    [Fact]
    public async Task Release_names_exactly_one_target()
    {
        using var client = await SuperAdminAsync();
        (await client.PostAsync(Release, Body(new { }))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsync(Release, Body(new { lockId = Guid.CreateVersion7(), deviceIds = new[] { Guid.CreateVersion7() } })))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ---------------------------------------------------------------- shape

    [Fact]
    public async Task The_target_shape_and_text_limits_are_enforced_with_400()
    {
        using var client = await SuperAdminAsync();
        await SetCodeAsync(client);
        var device = await _support.SeedDeviceAsync(agentVersion: CurrentAgent);
        var group = await _support.CreateGroupAsync(client, UniqueName("LkShape"), device);

        async Task<HttpStatusCode> Post(object body) => (await client.PostAsync(Locks, Body(body))).StatusCode;

        (await Post(new { message = "Hi" })).ShouldBe(HttpStatusCode.BadRequest);
        (await Post(new { message = "Hi", groupId = group, deviceIds = new[] { device } })).ShouldBe(HttpStatusCode.BadRequest);
        (await Post(new { message = "", deviceIds = new[] { device } })).ShouldBe(HttpStatusCode.BadRequest);
        (await Post(new { message = new string('m', 501), deviceIds = new[] { device } })).ShouldBe(HttpStatusCode.BadRequest);
        (await Post(new { message = "Hi", contactLine = new string('c', 201), deviceIds = new[] { device } }))
            .ShouldBe(HttpStatusCode.BadRequest);

        (await _support.TasksAsync(device, DeviceTaskType.EngageScreenLock)).ShouldBe(0, "a refused request queues nothing");
    }

    [Fact]
    public async Task Control_characters_in_the_message_or_contact_line_are_refused()
    {
        using var client = await SuperAdminAsync();
        await SetCodeAsync(client);
        var device = await _support.SeedDeviceAsync(agentVersion: CurrentAgent);

        (await client.PostAsync(Locks, Body(new
        {
            message = "Ding \u0007 dong.",
            deviceIds = new[] { device },
        }))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await client.PostAsync(Locks, Body(new
        {
            message = "Fine.",
            contactLine = "Bell \u0007 line.",
            deviceIds = new[] { device },
        }))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await _support.TasksAsync(device, DeviceTaskType.EngageScreenLock)).ShouldBe(0);

        var ok = await EngagedAsync(await client.PostAsync(Locks, Body(new
        {
            message = "Line one.\nLine two.",
            deviceIds = new[] { device },
        })));
        ok.Queued.ShouldBe(1, "a line break is ordinary lock text");
    }

    // ------------------------------------------------------------- security

    /// <summary>
    /// The persistent screen lock is its OWN authority (device.screenlock), not the
    /// session lock's (device.lock): an IT administrator may lock, but Helpdesk --
    /// which holds device.lock for the session lock -- may not, and neither may a
    /// read-only viewer or an anonymous caller.
    /// </summary>
    [Fact]
    public async Task Only_holders_of_device_screenlock_may_lock_release_or_manage_the_code()
    {
        using var admin = await SuperAdminAsync();
        await SetCodeAsync(admin);
        var forbiddenDevice = await _support.SeedDeviceAsync(agentVersion: CurrentAgent);

        // An administrator holding software.view and nothing else -- a viewer.
        var email = $"lock-viewer-{Guid.CreateVersion7():N}@test.local";
        await using (var db = _fixture.CreateDbContext())
        {
            var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
            var role = Role.CreateCustom(
                org.Id, $"lock_viewer_{Guid.CreateVersion7():N}", "Lock Viewer", "software.view only");
            var view = await db.Permissions.SingleAsync(p => p.Key == Permissions.Software.View);
            role.GrantPermission(view.Id);
            db.Roles.Add(role);
            await db.SaveChangesAsync();

            var user = new PlatformUser(org.Id, email, "Lock Viewer");
            user.SetPasswordHash(
                Infrastructure.Security.PasswordHasher.Hash(AdminApiPostgresFixture.Password), DateTimeOffset.UtcNow);
            AdminApiPostgresFixture.EnrolMfa(user);
            user.AssignRole(role.Id);
            user.GrantAllDeviceScope();
            db.PlatformUsers.Add(user);
            await db.SaveChangesAsync();
        }

        var engageBody = new { message = "Nope.", deviceIds = new[] { forbiddenDevice } };

        async Task ForbiddenEverywhere(HttpClient c)
        {
            (await c.PostAsync(Locks, Body(engageBody))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await c.PostAsync(Release, Body(new { deviceIds = new[] { forbiddenDevice } }))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await c.GetAsync(Locks)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await c.PutAsync(UnlockCode, Body(new { code = TheCode }))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await c.GetAsync(UnlockCode)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using var viewer = _fixture.CreateClientFor(await _fixture.SignInAsync(email));
        await ForbiddenEverywhere(viewer);

        // Helpdesk holds device.lock (the session lock) but NOT device.screenlock,
        // so every persistent-lock route is forbidden to it. This is the whole point
        // of the split.
        using var helpdesk = await _support.ClientAsAsync(AdminApiPostgresFixture.HelpdeskEmail);
        await ForbiddenEverywhere(helpdesk);

        using var anonymous = _fixture.Factory.CreateClient();
        (await anonymous.PostAsync(Locks, Body(engageBody))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync(Locks)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await _support.TasksAsync(forbiddenDevice, DeviceTaskType.EngageScreenLock)).ShouldBe(0);

        // IT Administrator holds device.screenlock and can lock a device of its own.
        using var itAdmin = await _support.ClientAsAsync(AdminApiPostgresFixture.ItAdminEmail);
        var itDevice = await _support.SeedDeviceAsync(agentVersion: CurrentAgent);
        var engaged = await EngagedAsync(await itAdmin.PostAsync(Locks, Body(new
        {
            message = "IT administrator can lock.",
            deviceIds = new[] { itDevice },
        })));
        engaged.Queued.ShouldBe(1);
        (await itAdmin.GetAsync(Locks)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ----------------------------------------------------------------- list

    [Fact]
    public async Task The_history_lists_the_lock_with_its_counts()
    {
        using var client = await SuperAdminAsync();
        await SetCodeAsync(client);
        var current = await _support.SeedDeviceAsync(agentVersion: CurrentAgent);
        var old = await _support.SeedDeviceAsync(agentVersion: "1.18.0");
        var groupName = UniqueName("LkList");
        var group = await _support.CreateGroupAsync(client, groupName, current, old);

        var engaged = await EngagedAsync(await client.PostAsync(Locks, Body(new
        {
            message = "History check.",
            contactLine = "x1234",
            groupId = group,
        })));
        await DeliverEngageAsync(current);

        var rows = await (await client.GetAsync(Locks)).Content.ReadFromJsonAsync<JsonElement>();
        var row = rows.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == engaged.LockId);

        row.GetProperty("message").GetString().ShouldBe("History check.");
        row.GetProperty("contactLine").GetString().ShouldBe("x1234");
        row.GetProperty("targetType").GetString().ShouldBe("Group");
        row.GetProperty("groupName").GetString().ShouldBe(groupName);
        row.GetProperty("createdByDisplay").GetString().ShouldBe(AdminApiPostgresFixture.SuperAdminEmail);
        row.GetProperty("targeted").GetInt32().ShouldBe(2);
        row.GetProperty("locked").GetInt32().ShouldBe(1, "the delivered engage is holding the lock");
        row.GetProperty("skipped").GetInt32().ShouldBe(1, "the old agent was skipped");
        row.GetProperty("released").GetInt32().ShouldBe(0);
    }

    /// <summary>
    /// The offline tally counts only devices still waiting to lock on reconnect, not
    /// every stale device: a device whose lock FAILED while it was offline is counted
    /// as failed, never also as offline.
    /// </summary>
    [Fact]
    public async Task The_offline_tally_excludes_a_device_whose_lock_failed_while_offline()
    {
        using var client = await SuperAdminAsync();
        await SetCodeAsync(client);
        var waiting = await _support.SeedDeviceAsync(online: false, agentVersion: CurrentAgent);
        var failed = await _support.SeedDeviceAsync(online: false, agentVersion: CurrentAgent);

        var engaged = await EngagedAsync(await client.PostAsync(Locks, Body(new
        {
            message = "Locked.",
            deviceIds = new[] { waiting, failed },
        })));
        engaged.Offline.ShouldBe(2, "both were offline at engage time");

        // One device's lock fails; it is still offline.
        await FailEngageAsync(failed);

        var rows = await (await client.GetAsync(Locks)).Content.ReadFromJsonAsync<JsonElement>();
        var row = rows.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == engaged.LockId);

        row.GetProperty("targeted").GetInt32().ShouldBe(2);
        row.GetProperty("pending").GetInt32().ShouldBe(1, "only the still-queued device is waiting to lock");
        row.GetProperty("failed").GetInt32().ShouldBe(1);
        row.GetProperty("offline").GetInt32().ShouldBe(1,
            "the failed-and-offline device must not be counted offline too");
        row.GetProperty("locked").GetInt32().ShouldBe(0);
    }

    // --------------------------------------------- derived device-list state

    /// <summary>
    /// The device list reports "locked" derived from the tasks and the heartbeat:
    /// a delivered engage reads as locked, and the agent then reporting it unlocked
    /// (the on-device IT code) clears it with no release task queued.
    /// </summary>
    [Fact]
    public async Task The_device_list_reports_locked_and_a_heartbeat_reporting_unlocked_clears_it()
    {
        using var client = await SuperAdminAsync();
        await SetCodeAsync(client);
        var tag = Guid.CreateVersion7().ToString("N")[..10];
        var hostname = $"LOCK-{tag}";
        var device = await _support.SeedDeviceAsync(agentVersion: CurrentAgent, hostname: hostname);

        await EngagedAsync(await client.PostAsync(Locks, Body(new { message = "Locked.", deviceIds = new[] { device } })));
        await DeliverEngageAsync(device);

        (await ScreenLockedInListAsync(client, tag)).ShouldBeTrue("a delivered engage with no release reads as locked");

        // The agent reports it is no longer locked (local IT unlock code).
        await ReportScreenLockedAsync(device, false);
        (await ScreenLockedInListAsync(client, tag)).ShouldBeFalse("the agent's word that it is unlocked clears the derived status");

        // The agent reports it is locked again: back to locked without any new task.
        await ReportScreenLockedAsync(device, true);
        (await ScreenLockedInListAsync(client, tag)).ShouldBeTrue();
    }

    private async Task<bool> ScreenLockedInListAsync(HttpClient client, string search)
    {
        var page = await (await client.GetAsync(new Uri($"/admin/v1/devices?search={search}", UriKind.Relative)))
            .Content.ReadFromJsonAsync<JsonElement>();
        return page.GetProperty("items").EnumerateArray().Single().GetProperty("screenLocked").GetBoolean();
    }

    // ----------------------------------------------------------------- seeding

    private async Task<Guid> OrgOfAsync(string email)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.PlatformUsers.Where(u => u.Email == email).Select(u => u.OrganizationId).SingleAsync();
    }

    /// <summary>An organization of its own, with its own signed-in super admin and a current-agent device.</summary>
    private async Task<(HttpClient Client, Guid DeviceId)> SeedIsolatedOrgAdminAndDeviceAsync()
    {
        var email = $"lock-iso-{Guid.CreateVersion7():N}@test.local";
        Guid deviceId;
        await using (var db = _fixture.CreateDbContext())
        {
            var org = new Organization("Iso Org", ("a" + Guid.CreateVersion7().ToString("N"))[..20]);
            db.Organizations.Add(org);
            await db.SaveChangesAsync();

            var superAdminRoleId = await db.Roles
                .Where(r => r.Key == SystemRoles.SuperAdministrator && r.IsBuiltIn)
                .Select(r => r.Id)
                .SingleAsync();

            var user = new PlatformUser(org.Id, email, "Iso Admin");
            user.SetPasswordHash(
                Infrastructure.Security.PasswordHasher.Hash(AdminApiPostgresFixture.Password), DateTimeOffset.UtcNow);
            AdminApiPostgresFixture.EnrolMfa(user);
            user.AssignRole(superAdminRoleId);
            user.GrantAllDeviceScope();
            db.PlatformUsers.Add(user);

            var token = new EnrollmentToken(
                org.Id, $"lock-iso-{Guid.CreateVersion7():N}",
                Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
                user.Id, "seed", DateTimeOffset.UtcNow.AddHours(1), 1);
            db.EnrollmentTokens.Add(token);
            await db.SaveChangesAsync();

            var device = Device.Enroll(
                org.Id, "ISO-PC", "m-" + Guid.CreateVersion7().ToString("N"),
                CurrentAgent, "Windows 11 Pro", token.Id, DateTimeOffset.UtcNow);
            db.Devices.Add(device);
            await db.SaveChangesAsync();
            deviceId = device.Id;
        }

        var client = _fixture.CreateClientFor(await _fixture.SignInAsync(email));
        return (client, deviceId);
    }
}
