using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using EndpointPlatform.Domain.Peripherals;
using EndpointPlatform.Domain.Policies;
using EndpointPlatform.Domain.Tasks;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Device scope on the routes the 2026-10-24 audit found scoped by organization
/// only: USB access, device detail/list/rename, fleet task history, policy
/// assignment and compliance, and package deployment -- plus the 403-vs-404
/// standardisation on the local-account routes.
/// </summary>
/// <remarks>
/// <para>
/// Every scenario uses the same shape: one device in a group the scoped
/// administrator holds, one device outside it, an IT Administrator scoped to that
/// group only, and the fixture's IT Administrator (all-device scope) as the
/// unscoped baseline. The scoped administrator holds every permission the routes
/// need, so any refusal here is scope and nothing else.
/// </para>
/// <para>
/// Out of scope must be 404 -- the same answer as a device that does not exist --
/// and must change nothing. Each mutation test reads the database afterwards to
/// prove the second half, because a 404 returned after the write would still be a
/// breach.
/// </para>
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class DeviceScopeEnforcementTests(AdminApiPostgresFixture fixture)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;
    private readonly GroupTestSupport _groups = new(fixture);

    private sealed record Scenario(Guid InScope, Guid OutOfScope, Guid GroupId, HttpClient Scoped, HttpClient Unscoped)
        : IDisposable
    {
        public void Dispose()
        {
            Scoped.Dispose();
            Unscoped.Dispose();
        }
    }

    /// <summary>
    /// One in-scope device in a fresh group, one out-of-scope device left in
    /// "All Devices", an administrator scoped to the fresh group only, and the
    /// all-device IT Administrator.
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        // Random (v4) hostnames, not the helper's default: that one is cut from a
        // time-ordered v7 GUID, so two devices seeded in the same minute share a
        // name and a search for one finds both.
        var inScope = await _groups.SeedDeviceAsync(hostname: $"INS-{Guid.NewGuid():N}"[..15]);
        var outOfScope = await _groups.SeedDeviceAsync(hostname: $"OOS-{Guid.NewGuid():N}"[..15]);

        var unscoped = await _groups.ClientAsAsync(AdminApiPostgresFixture.ItAdminEmail);
        var groupId = await _groups.CreateGroupAsync(unscoped, GroupTestSupport.UniqueName("Scope"), inScope);
        var scoped = await _groups.ScopedAdminAsync(groupId);

        return new Scenario(inScope, outOfScope, groupId, scoped, unscoped);
    }

    private static Uri Rel(string path) => new(path, UriKind.Relative);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static HashSet<Guid> Ids(JsonElement array, string property) =>
        array.EnumerateArray().Select(e => e.GetProperty(property).GetGuid()).ToHashSet();

    /// <summary>Set equality, compared as sorted text so a failure prints both sides readably.</summary>
    private static void ShouldBeExactly(IEnumerable<Guid> actual, Guid[] expected, string? because = null) =>
        string.Join(", ", actual.Distinct().Order()).ShouldBe(string.Join(", ", expected.Distinct().Order()), because);

    private async Task<Guid> OrganizationIdAsync()
    {
        await using var db = _fixture.CreateDbContext();
        return await db.Organizations.OrderBy(o => o.CreatedAt).Select(o => o.Id).FirstAsync();
    }

    private async Task<int> TaskCountAsync(Guid deviceId, DeviceTaskType type)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.DeviceTasks.CountAsync(t => t.DeviceId == deviceId && t.Type == type);
    }

    // ===================================================================== S1 USB

    private async Task<Guid> SeedUsbStorageAsync(Guid deviceId)
    {
        await using var db = _fixture.CreateDbContext();
        var usb = new UsbDevice(
            await OrganizationIdAsync(), deviceId, $@"USB\VID_0781&PID_5581\SCOPE{Guid.CreateVersion7():N}"[..40],
            UsbDeviceClass.Storage, "0781", "5581", "ABC123", "SanDisk", "Cruzer Fit", null, DateTimeOffset.UtcNow);
        db.UsbDevices.Add(usb);
        await db.SaveChangesAsync();
        return usb.Id;
    }

    private static Uri UsbGrant(Guid deviceId, Guid usbId) => Rel($"/admin/v1/devices/{deviceId}/usb-devices/{usbId}/grant");

    private static JsonContent UsbGrantBody() =>
        JsonContent.Create(new { durationMinutes = 60, justification = "Scope enforcement test." });

    private static Uri UsbRevoke(Guid requestId) => Rel($"/admin/v1/usb-access-requests/{requestId}/revoke");

    [Fact]
    public async Task Usb_grant_on_an_out_of_scope_device_is_not_found_and_grants_nothing()
    {
        using var s = await ArrangeAsync();
        var usbId = await SeedUsbStorageAsync(s.OutOfScope);

        (await s.Scoped.PostAsync(UsbGrant(s.OutOfScope, usbId), UsbGrantBody()))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound, "usb.manage must not reach a device outside the caller's groups");

        await using var db = _fixture.CreateDbContext();
        (await db.UsbAccessRequests.AnyAsync(r => r.DeviceId == s.OutOfScope)).ShouldBeFalse();
        (await db.UsbDevices.AsNoTracking().SingleAsync(u => u.Id == usbId)).Policy.ShouldBe(UsbStoragePolicy.Restricted);
        (await TaskCountAsync(s.OutOfScope, DeviceTaskType.ApplyUsbPolicy)).ShouldBe(0);
    }

    [Fact]
    public async Task Usb_grant_on_an_in_scope_device_still_works_for_a_scoped_administrator()
    {
        using var s = await ArrangeAsync();
        var usbId = await SeedUsbStorageAsync(s.InScope);

        var response = await s.Scoped.PostAsync(UsbGrant(s.InScope, usbId), UsbGrantBody());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await TaskCountAsync(s.InScope, DeviceTaskType.ApplyUsbPolicy)).ShouldBe(1);
    }

    [Fact]
    public async Task Usb_reapply_is_scoped()
    {
        using var s = await ArrangeAsync();

        (await s.Scoped.PostAsync(Rel($"/admin/v1/devices/{s.OutOfScope}/usb-devices/reapply"), null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await TaskCountAsync(s.OutOfScope, DeviceTaskType.ApplyUsbPolicy)).ShouldBe(0, "nothing may be queued out of scope");

        (await s.Scoped.PostAsync(Rel($"/admin/v1/devices/{s.InScope}/usb-devices/reapply"), null))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);

        (await s.Unscoped.PostAsync(Rel($"/admin/v1/devices/{s.OutOfScope}/usb-devices/reapply"), null))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted, "an all-device administrator is unchanged");
    }

    [Fact]
    public async Task Usb_revoke_is_scoped_by_the_grants_own_device()
    {
        using var s = await ArrangeAsync();
        var outUsb = await SeedUsbStorageAsync(s.OutOfScope);
        var inUsb = await SeedUsbStorageAsync(s.InScope);

        // Granted by the all-device administrator, so the out-of-scope grant exists.
        var outGrant = (await JsonAsync(await s.Unscoped.PostAsync(UsbGrant(s.OutOfScope, outUsb), UsbGrantBody())))
            .GetProperty("requestId").GetGuid();
        var inGrant = (await JsonAsync(await s.Unscoped.PostAsync(UsbGrant(s.InScope, inUsb), UsbGrantBody())))
            .GetProperty("requestId").GetGuid();

        (await s.Scoped.PostAsync(UsbRevoke(outGrant), JsonContent.Create(new { note = "not mine" })))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound, "quoting another group's grant id must not reach it");

        await using (var db = _fixture.CreateDbContext())
        {
            var untouched = await db.UsbAccessRequests.AsNoTracking().SingleAsync(r => r.Id == outGrant);
            untouched.Status.ShouldBe(UsbAccessRequestStatus.Approved);
            untouched.IsLive(DateTimeOffset.UtcNow).ShouldBeTrue("an out-of-scope revoke must change nothing");
        }

        // An unknown grant id answers exactly as the out-of-scope one does.
        (await s.Scoped.PostAsync(UsbRevoke(Guid.CreateVersion7()), JsonContent.Create(new { note = (string?)null })))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await s.Scoped.PostAsync(UsbRevoke(inGrant), JsonContent.Create(new { note = (string?)null })))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent, "the scoped administrator still revokes their own");

        (await s.Unscoped.PostAsync(UsbRevoke(outGrant), JsonContent.Create(new { note = (string?)null })))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent, "an all-device administrator is unchanged");
    }

    [Fact]
    public async Task Usb_lists_leave_out_of_scope_devices_out()
    {
        using var s = await ArrangeAsync();
        var outUsb = await SeedUsbStorageAsync(s.OutOfScope);
        var inUsb = await SeedUsbStorageAsync(s.InScope);

        var outGrant = (await JsonAsync(await s.Unscoped.PostAsync(UsbGrant(s.OutOfScope, outUsb), UsbGrantBody())))
            .GetProperty("requestId").GetGuid();
        var inGrant = (await JsonAsync(await s.Unscoped.PostAsync(UsbGrant(s.InScope, inUsb), UsbGrantBody())))
            .GetProperty("requestId").GetGuid();

        // Connected peripherals across the fleet.
        var scopedConnected = await JsonAsync(await s.Scoped.GetAsync(Rel("/admin/v1/usb-devices/connected")));
        ShouldBeExactly(Ids(scopedConnected, "deviceId"), [s.InScope], "a scoped administrator sees only their own machines");
        Ids(scopedConnected, "id").ShouldContain(inUsb);

        var allConnected = await JsonAsync(await s.Unscoped.GetAsync(Rel("/admin/v1/usb-devices/connected")));
        Ids(allConnected, "id").ShouldContain(inUsb);
        Ids(allConnected, "id").ShouldContain(outUsb);

        // The access ledger.
        var scopedRequests = await JsonAsync(await s.Scoped.GetAsync(Rel("/admin/v1/usb-access-requests?limit=500")));
        ShouldBeExactly(Ids(scopedRequests, "deviceId"), [s.InScope]);
        Ids(scopedRequests, "id").ShouldContain(inGrant);

        var scopedLive = await JsonAsync(await s.Scoped.GetAsync(Rel("/admin/v1/usb-access-requests?liveOnly=true&limit=500")));
        Ids(scopedLive, "id").ShouldNotContain(outGrant);

        var allRequests = await JsonAsync(await s.Unscoped.GetAsync(Rel("/admin/v1/usb-access-requests?limit=500")));
        Ids(allRequests, "id").ShouldContain(inGrant);
        Ids(allRequests, "id").ShouldContain(outGrant);

        // Per device.
        (await s.Scoped.GetAsync(Rel($"/admin/v1/devices/{s.OutOfScope}/usb-devices")))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        Ids(await JsonAsync(await s.Scoped.GetAsync(Rel($"/admin/v1/devices/{s.InScope}/usb-devices"))), "id")
            .ShouldContain(inUsb);
        Ids(await JsonAsync(await s.Unscoped.GetAsync(Rel($"/admin/v1/devices/{s.OutOfScope}/usb-devices"))), "id")
            .ShouldContain(outUsb);
    }

    // ================================================================= S2 devices

    [Fact]
    public async Task Device_detail_is_scoped()
    {
        using var s = await ArrangeAsync();

        (await s.Scoped.GetAsync(Rel($"/admin/v1/devices/{s.OutOfScope}")))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound, "the detail carries local users and software inventory");

        (await JsonAsync(await s.Scoped.GetAsync(Rel($"/admin/v1/devices/{s.InScope}"))))
            .GetProperty("id").GetGuid().ShouldBe(s.InScope);

        (await JsonAsync(await s.Unscoped.GetAsync(Rel($"/admin/v1/devices/{s.OutOfScope}"))))
            .GetProperty("id").GetGuid().ShouldBe(s.OutOfScope, "an all-device administrator is unchanged");
    }

    [Fact]
    public async Task Device_rename_is_scoped_and_an_out_of_scope_rename_changes_nothing()
    {
        using var s = await ArrangeAsync();
        static JsonContent Name(string value) => JsonContent.Create(new { displayName = value });

        (await s.Scoped.PatchAsync(Rel($"/admin/v1/devices/{s.OutOfScope}/display-name"), Name("Not-Yours")))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await using (var db = _fixture.CreateDbContext())
        {
            (await db.Devices.AsNoTracking().SingleAsync(d => d.Id == s.OutOfScope)).DisplayName.ShouldBeNull();
            (await db.AuditLogEntries.AnyAsync(a => a.DeviceId == s.OutOfScope && a.Action == "device.rename"))
                .ShouldBeFalse();
        }

        (await s.Scoped.PatchAsync(Rel($"/admin/v1/devices/{s.InScope}/display-name"), Name("Mine-01")))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using (var db = _fixture.CreateDbContext())
        {
            (await db.Devices.AsNoTracking().SingleAsync(d => d.Id == s.InScope)).DisplayName.ShouldBe("Mine-01");
        }
    }

    [Fact]
    public async Task Device_list_and_counts_describe_only_in_scope_devices()
    {
        using var s = await ArrangeAsync();

        var page = await JsonAsync(await s.Scoped.GetAsync(Rel("/admin/v1/devices?pageSize=200")));
        ShouldBeExactly(Ids(page.GetProperty("items"), "id"), [s.InScope]);
        page.GetProperty("totalCount").GetInt32().ShouldBe(1, "the total must not reveal the size of the rest of the estate");

        var counts = await JsonAsync(await s.Scoped.GetAsync(Rel("/admin/v1/devices/counts")));
        counts.GetProperty("total").GetInt32().ShouldBe(1);
        (counts.GetProperty("online").GetInt32() + counts.GetProperty("offline").GetInt32()
            + counts.GetProperty("retired").GetInt32()).ShouldBe(1);

        // Searching for the out-of-scope machine by name finds nothing for the
        // scoped administrator and finds it for the all-device one.
        string outHostname;
        await using (var db = _fixture.CreateDbContext())
        {
            outHostname = await db.Devices.Where(d => d.Id == s.OutOfScope).Select(d => d.Hostname).SingleAsync();
        }

        var scopedSearch = await JsonAsync(await s.Scoped.GetAsync(Rel($"/admin/v1/devices?search={outHostname}")));
        scopedSearch.GetProperty("totalCount").GetInt32().ShouldBe(0);

        var unscopedSearch = await JsonAsync(await s.Unscoped.GetAsync(Rel($"/admin/v1/devices?search={outHostname}")));
        Ids(unscopedSearch.GetProperty("items"), "id").ShouldContain(s.OutOfScope);

        var allCounts = await JsonAsync(await s.Unscoped.GetAsync(Rel("/admin/v1/devices/counts")));
        allCounts.GetProperty("total").GetInt32().ShouldBeGreaterThanOrEqualTo(2, "an all-device administrator is unchanged");
    }

    [Fact]
    public async Task An_administrator_with_no_scope_at_all_lists_and_counts_nothing()
    {
        await _groups.SeedDeviceAsync();

        // Deny by default: no scope rows means no device, never every device.
        using var nobody = await _groups.ScopedAdminAsync();

        var page = await JsonAsync(await nobody.GetAsync(Rel("/admin/v1/devices?pageSize=200")));
        page.GetProperty("items").GetArrayLength().ShouldBe(0);
        page.GetProperty("totalCount").GetInt32().ShouldBe(0);

        (await JsonAsync(await nobody.GetAsync(Rel("/admin/v1/devices/counts"))))
            .GetProperty("total").GetInt32().ShouldBe(0);

        (await JsonAsync(await nobody.GetAsync(Rel("/admin/v1/tasks?pageSize=100"))))
            .GetProperty("totalCount").GetInt32().ShouldBe(0);
    }

    // ================================================================== S3 tasks

    [Fact]
    public async Task Fleet_task_history_contains_only_in_scope_devices()
    {
        using var s = await ArrangeAsync();

        var inTask = (await JsonAsync(await s.Unscoped.PostAsync(Rel($"/admin/v1/devices/{s.InScope}/actions/lock"), null)))
            .GetProperty("taskId").GetGuid();
        var outTask = (await JsonAsync(await s.Unscoped.PostAsync(Rel($"/admin/v1/devices/{s.OutOfScope}/actions/lock"), null)))
            .GetProperty("taskId").GetGuid();

        var scoped = await JsonAsync(await s.Scoped.GetAsync(Rel("/admin/v1/tasks?pageSize=100")));
        var scopedItems = scoped.GetProperty("items");
        ShouldBeExactly(Ids(scopedItems, "deviceId"), [s.InScope], "a scoped administrator sees only work on their own machines");
        Ids(scopedItems, "id").ShouldContain(inTask);
        Ids(scopedItems, "id").ShouldNotContain(outTask);
        scoped.GetProperty("totalCount").GetInt32().ShouldBe(scopedItems.GetArrayLength());

        // The per-device history was already scoped; pinned here alongside.
        (await s.Scoped.GetAsync(Rel($"/admin/v1/devices/{s.OutOfScope}/tasks")))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var all = await JsonAsync(await s.Unscoped.GetAsync(Rel("/admin/v1/tasks?pageSize=100")));
        Ids(all.GetProperty("items"), "id").ShouldContain(inTask);
        Ids(all.GetProperty("items"), "id").ShouldContain(outTask);
    }

    // =============================================================== S4 policies

    private async Task<Guid> CreatePolicyAsync()
    {
        using var super = await _groups.ClientAsAsync(AdminApiPostgresFixture.SuperAdminEmail);
        var response = await super.PostAsJsonAsync(Rel("/admin/v1/policies"), new
        {
            type = "ScreenLockTimeout",
            name = GroupTestSupport.UniqueName("Scope-Policy"),
            description = "Scope enforcement test.",
            maxTimeoutSeconds = 600,
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<bool> AssignedAsync(Guid policyId, Guid targetId)
    {
        await using var db = _fixture.CreateDbContext();
        return await db.PolicyAssignments.AnyAsync(a => a.PolicyId == policyId && a.TargetId == targetId);
    }

    [Fact]
    public async Task Policy_assignment_to_a_device_is_scoped()
    {
        using var s = await ArrangeAsync();
        var policyId = await CreatePolicyAsync();

        (await s.Scoped.PostAsJsonAsync(Rel($"/admin/v1/policies/{policyId}/assign"), new { deviceId = s.OutOfScope }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await AssignedAsync(policyId, s.OutOfScope)).ShouldBeFalse("an out-of-scope assignment must change nothing");

        (await s.Scoped.PostAsJsonAsync(Rel($"/admin/v1/policies/{policyId}/assign"), new { deviceId = s.InScope }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await AssignedAsync(policyId, s.InScope)).ShouldBeTrue();

        (await s.Unscoped.PostAsJsonAsync(Rel($"/admin/v1/policies/{policyId}/assign"), new { deviceId = s.OutOfScope }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent, "an all-device administrator is unchanged");
    }

    [Fact]
    public async Task Policy_assignment_to_a_group_is_scoped()
    {
        using var s = await ArrangeAsync();
        var policyId = await CreatePolicyAsync();
        var otherGroup = await _groups.CreateGroupAsync(s.Unscoped, GroupTestSupport.UniqueName("Other"), s.OutOfScope);

        (await s.Scoped.PostAsJsonAsync(Rel($"/admin/v1/policies/{policyId}/assign-group"), new { groupId = otherGroup }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await AssignedAsync(policyId, otherGroup)).ShouldBeFalse();

        (await s.Scoped.PostAsJsonAsync(Rel($"/admin/v1/policies/{policyId}/assign-group"), new { groupId = Guid.CreateVersion7() }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound, "unknown and out of scope are the same answer");

        (await s.Scoped.PostAsJsonAsync(Rel($"/admin/v1/policies/{policyId}/assign-group"), new { groupId = s.GroupId }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await AssignedAsync(policyId, s.GroupId)).ShouldBeTrue();
    }

    [Fact]
    public async Task Policy_compliance_reads_are_scoped()
    {
        using var s = await ArrangeAsync();
        var policyId = await CreatePolicyAsync();

        await using (var db = _fixture.CreateDbContext())
        {
            var organizationId = await db.Organizations.OrderBy(o => o.CreatedAt).Select(o => o.Id).FirstAsync();
            var versionId = await db.PolicyVersions.Where(v => v.PolicyId == policyId).Select(v => v.Id).FirstAsync();

            foreach (var (deviceId, state) in new[]
                     {
                         (s.InScope, PolicyComplianceState.Compliant),
                         (s.OutOfScope, PolicyComplianceState.NonCompliant),
                     })
            {
                var result = new PolicyComplianceResult(organizationId, deviceId, policyId);
                result.Record(versionId, 1, state, null, DateTimeOffset.UtcNow);
                db.PolicyComplianceResults.Add(result);
            }

            await db.SaveChangesAsync();
        }

        var scopedRows = await JsonAsync(await s.Scoped.GetAsync(Rel($"/admin/v1/policies/{policyId}/compliance")));
        ShouldBeExactly(Ids(scopedRows, "deviceId"), [s.InScope]);

        var allRows = await JsonAsync(await s.Unscoped.GetAsync(Rel($"/admin/v1/policies/{policyId}/compliance")));
        ShouldBeExactly(Ids(allRows, "deviceId"), [s.InScope, s.OutOfScope]);

        static int Evaluated(JsonElement policies, Guid id)
        {
            var row = policies.EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == id);
            return row.GetProperty("compliant").GetInt32()
                   + row.GetProperty("nonCompliant").GetInt32()
                   + row.GetProperty("unknown").GetInt32();
        }

        var scopedList = await JsonAsync(await s.Scoped.GetAsync(Rel("/admin/v1/policies")));
        Evaluated(scopedList, policyId).ShouldBe(1, "rollups count only the caller's devices");
        scopedList.EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == policyId)
            .GetProperty("nonCompliant").GetInt32().ShouldBe(0, "the out-of-scope NonCompliant row is not counted");

        Evaluated(await JsonAsync(await s.Unscoped.GetAsync(Rel("/admin/v1/policies"))), policyId)
            .ShouldBe(2, "an all-device administrator is unchanged");
    }

    // ======================================================== sweep: packages

    private async Task<Guid> SeedPackageAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var user = await db.PlatformUsers.OrderBy(u => u.CreatedAt).FirstAsync();

        var package = new Domain.Software.SoftwarePackage(
            await OrganizationIdAsync(),
            $"Scope App {Guid.CreateVersion7():N}"[..24],
            "1.0.0",
            "Test Publisher",
            Domain.Software.SoftwarePackageType.WindowsInstaller,
            Convert.ToHexStringLower(SHA256.HashData(Guid.CreateVersion7().ToByteArray())),
            "app.msi",
            2048,
            $"{{{Guid.NewGuid().ToString().ToUpperInvariant()}}}",
            requiredSignerSubject: null,
            user.Id,
            user.Email);

        db.SoftwarePackages.Add(package);
        await db.SaveChangesAsync();
        return package.Id;
    }

    [Fact]
    public async Task Package_deploy_is_scoped_for_both_device_and_group_targets()
    {
        using var s = await ArrangeAsync();
        var packageId = await SeedPackageAsync();
        var deploy = Rel($"/admin/v1/packages/{packageId}/deploy");
        var otherGroup = await _groups.CreateGroupAsync(s.Unscoped, GroupTestSupport.UniqueName("PkgOther"), s.OutOfScope);

        (await s.Scoped.PostAsJsonAsync(deploy, new { deviceId = s.OutOfScope }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await s.Scoped.PostAsJsonAsync(deploy, new { groupId = otherGroup }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await TaskCountAsync(s.OutOfScope, DeviceTaskType.InstallPackage))
            .ShouldBe(0, "nothing may be installed out of scope");

        (await s.Scoped.PostAsJsonAsync(deploy, new { deviceId = s.InScope }))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await s.Scoped.PostAsJsonAsync(deploy, new { groupId = s.GroupId }))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await TaskCountAsync(s.InScope, DeviceTaskType.InstallPackage)).ShouldBe(2);

        (await s.Unscoped.PostAsJsonAsync(deploy, new { deviceId = s.OutOfScope }))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted, "an all-device administrator is unchanged");
    }

    // ============================================== S5 one answer for "not yours"

    /// <summary>
    /// Status and body with the per-request correlation id removed, so two
    /// responses can be compared for "indistinguishable".
    /// </summary>
    private static async Task<string> FingerprintAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            return $"{(int)response.StatusCode}:";
        }

        using var document = JsonDocument.Parse(text);
        var fields = document.RootElement.EnumerateObject()
            .Where(p => p.Name is not ("correlationId" or "traceId"))
            .ToDictionary(p => p.Name, p => p.Value.ToString());
        return $"{(int)response.StatusCode}:{JsonSerializer.Serialize(fields)}";
    }

    [Fact]
    public async Task Out_of_scope_and_nonexistent_devices_get_the_same_404_everywhere()
    {
        using var s = await ArrangeAsync();
        var unknown = Guid.CreateVersion7();
        const string AnySid = "S-1-5-21-1-2-3-1001";

        var probes = new (string Name, Func<Guid, Task<HttpResponseMessage>> Send)[]
        {
            ("device detail", id => s.Scoped.GetAsync(Rel($"/admin/v1/devices/{id}"))),
            ("device rename", id => s.Scoped.PatchAsync(
                Rel($"/admin/v1/devices/{id}/display-name"), JsonContent.Create(new { displayName = "x" }))),
            ("usb list", id => s.Scoped.GetAsync(Rel($"/admin/v1/devices/{id}/usb-devices"))),
            ("usb reapply", id => s.Scoped.PostAsync(Rel($"/admin/v1/devices/{id}/usb-devices/reapply"), null)),
            ("local users", id => s.Scoped.GetAsync(Rel($"/admin/v1/devices/{id}/local-users"))),
            ("local user", id => s.Scoped.GetAsync(Rel($"/admin/v1/devices/{id}/local-users/{AnySid}"))),
            ("local groups", id => s.Scoped.GetAsync(Rel($"/admin/v1/devices/{id}/local-groups"))),
            ("local admin posture", id => s.Scoped.GetAsync(Rel($"/admin/v1/devices/{id}/local-admin-posture"))),
            ("disable local user", id => s.Scoped.PostAsync(Rel($"/admin/v1/devices/{id}/local-users/{AnySid}/disable"), null)),
            ("delete local user", id => s.Scoped.DeleteAsync(Rel($"/admin/v1/devices/{id}/local-users/{AnySid}"))),
        };

        foreach (var (name, send) in probes)
        {
            var outOfScope = await send(s.OutOfScope);
            var missing = await send(unknown);

            outOfScope.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{name}: out of scope must be 404, not 403");
            (await FingerprintAsync(outOfScope)).ShouldBe(
                await FingerprintAsync(missing), $"{name}: out of scope must be indistinguishable from not there");
        }

        (await TaskCountAsync(s.OutOfScope, DeviceTaskType.DisableLocalUser)).ShouldBe(0);
        (await TaskCountAsync(s.OutOfScope, DeviceTaskType.DeleteLocalUser)).ShouldBe(0);
    }
}
