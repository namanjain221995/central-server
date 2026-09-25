using System.Text.Json;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Devices;
using EndpointPlatform.Infrastructure.Hosting;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Tests.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Infrastructure.Tests.Devices;

/// <summary>A minimal settable clock, so we do not depend on a time-testing package.</summary>
file sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// Chrome Management phase 3: the daily inventory refresh sweep asks stale
/// devices for a fresh upload through the same flag the Refresh button sets,
/// leaves every other device alone, and records one audit row per organization
/// per batch.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class InventoryRefreshSweepTests(PostgresFixture fixture)
{
    private readonly PostgresFixture _fixture = fixture;

    // Pinned years before anything the sibling suites stamp (they use 2026), so
    // this suite's stale devices sort oldest in the shared database and a bounded
    // batch is filled by these rows and no others. The sweep is fleet-wide by
    // design; the tests must not be.
    private static readonly DateTimeOffset Start = new(2020, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static InventoryRefreshSweepService Sweep(
        EndpointPlatformDbContext db, TimeProvider time, int refreshAfterHours = 24) =>
        new(db, time,
            Options.Create(new InventoryRefreshOptions { RefreshAfterHours = refreshAfterHours }),
            new AuditWriter(db, time, new CorrelationIdAccessor(), new HttpContextAccessor()),
            NullLogger<InventoryRefreshSweepService>.Instance);

    /// <summary>An organization and an enrollment token to enrol its devices under.</summary>
    private static (Organization Org, Guid TokenId) SeedOrganization(EndpointPlatformDbContext db, string name)
    {
        var org = new Organization(name, ("r" + Guid.CreateVersion7().ToString("N"))[..18]);
        db.Organizations.Add(org);

        var token = new Domain.Enrollment.EnrollmentToken(org.Id, "t",
            Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(), "a@b", Start.AddHours(1), 9);
        db.EnrollmentTokens.Add(token);

        return (org, token.Id);
    }

    /// <summary>
    /// An Active device whose last inventory upload was <paramref name="collectedHoursAgo"/>
    /// hours before <see cref="Start"/>; null means it has never uploaded.
    /// </summary>
    private static Device SeedDevice(
        EndpointPlatformDbContext db, Guid organizationId, Guid tokenId, string hostname, double? collectedHoursAgo)
    {
        var device = Device.Enroll(organizationId, hostname, "m-" + Guid.CreateVersion7().ToString("N"), "1", null,
            tokenId, Start.AddDays(-30));

        if (collectedHoursAgo is { } hours)
        {
            device.RecordInventory(null, Start.AddHours(-hours));
        }

        db.Devices.Add(device);
        return device;
    }

    /// <summary>Reads the device back through a fresh context, so the assertion is about what was committed.</summary>
    private async Task<Device> ReloadAsync(Guid deviceId)
    {
        await using var verify = _fixture.CreateDbContext();
        return await verify.Devices.SingleAsync(d => d.Id == deviceId);
    }

    private async Task<List<AuditLogEntry>> SweepAuditEntriesAsync(params Guid[] organizationIds)
    {
        await using var verify = _fixture.CreateDbContext();
        return await verify.AuditLogEntries
            .Where(a => a.Action == InventoryRefreshSweepService.AuditAction && organizationIds.Contains(a.OrganizationId))
            .ToListAsync();
    }

    [Fact]
    public async Task A_device_whose_inventory_is_older_than_the_threshold_is_asked_for_a_fresh_one()
    {
        var time = new TestClock(Start);
        await using var db = _fixture.CreateDbContext(time);
        var (org, tokenId) = SeedOrganization(db, "IR1");
        var stale = SeedDevice(db, org.Id, tokenId, "STALE", collectedHoursAgo: 25);
        var fresh = SeedDevice(db, org.Id, tokenId, "FRESH", collectedHoursAgo: 1);
        await db.SaveChangesAsync();

        (await Sweep(db, time).SweepAsync(500)).ShouldBe(1);

        var staleAfter = await ReloadAsync(stale.Id);
        staleAfter.InventoryRequestedAt.ShouldBe(Start, "the request is stamped with the sweep's clock, not the wall clock");
        staleAfter.IsInventoryRefreshPending.ShouldBeTrue("the next heartbeat must tell the agent to upload");

        var freshAfter = await ReloadAsync(fresh.Id);
        freshAfter.InventoryRequestedAt.ShouldBeNull("an upload from an hour ago is not stale");
        freshAfter.IsInventoryRefreshPending.ShouldBeFalse();
    }

    [Fact]
    public async Task A_device_already_waiting_on_a_request_is_not_asked_again()
    {
        var time = new TestClock(Start);
        await using var db = _fixture.CreateDbContext(time);
        var (org, tokenId) = SeedOrganization(db, "IR2");
        var pending = SeedDevice(db, org.Id, tokenId, "PENDING", collectedHoursAgo: 25);

        // An administrator pressed Refresh an hour ago and the agent has not
        // answered yet: the request is newer than the last upload.
        var requestedAt = Start.AddHours(-1);
        pending.RequestInventoryRefresh(requestedAt);
        await db.SaveChangesAsync();

        (await Sweep(db, time).SweepAsync(500)).ShouldBe(0);

        (await ReloadAsync(pending.Id)).InventoryRequestedAt
            .ShouldBe(requestedAt, "re-stamping a request the agent has not yet answered is churn, not a refresh");
    }

    [Fact]
    public async Task A_request_already_answered_by_a_later_upload_does_not_block_the_next_one()
    {
        var time = new TestClock(Start);
        await using var db = _fixture.CreateDbContext(time);
        var (org, tokenId) = SeedOrganization(db, "IR3");

        // Requested 30 h ago, uploaded 25 h ago: that request was fulfilled, and
        // the upload it produced has since gone stale in its own right.
        var device = SeedDevice(db, org.Id, tokenId, "ANSWERED", collectedHoursAgo: 25);
        device.RequestInventoryRefresh(Start.AddHours(-30));
        await db.SaveChangesAsync();

        (await Sweep(db, time).SweepAsync(500)).ShouldBe(1);

        var after = await ReloadAsync(device.Id);
        after.InventoryRequestedAt.ShouldBe(Start);
        after.IsInventoryRefreshPending.ShouldBeTrue();
    }

    [Fact]
    public async Task A_device_that_has_never_uploaded_inventory_is_left_alone()
    {
        var time = new TestClock(Start);
        await using var db = _fixture.CreateDbContext(time);
        var (org, tokenId) = SeedOrganization(db, "IR4");
        var never = SeedDevice(db, org.Id, tokenId, "NEVER", collectedHoursAgo: null);
        await db.SaveChangesAsync();

        (await Sweep(db, time).SweepAsync(500)).ShouldBe(0);

        var after = await ReloadAsync(never.Id);
        after.InventoryRequestedAt.ShouldBeNull("nothing to request: a device with no upload is pending by construction");
        after.IsInventoryRefreshPending.ShouldBeTrue();
    }

    [Fact]
    public async Task A_retired_device_is_not_asked()
    {
        var time = new TestClock(Start);
        await using var db = _fixture.CreateDbContext(time);
        var (org, tokenId) = SeedOrganization(db, "IR5");
        var retired = SeedDevice(db, org.Id, tokenId, "RETIRED", collectedHoursAgo: 25);
        retired.Retire();
        await db.SaveChangesAsync();

        (await Sweep(db, time).SweepAsync(500)).ShouldBe(0);

        (await ReloadAsync(retired.Id)).InventoryRequestedAt
            .ShouldBeNull("a retired device no longer heartbeats, so a request to it would never be answered");
        (await SweepAuditEntriesAsync(org.Id)).ShouldBeEmpty("an empty batch records nothing");
    }

    [Fact]
    public async Task The_batch_size_is_honoured_and_the_oldest_devices_go_first()
    {
        var time = new TestClock(Start);
        await using var db = _fixture.CreateDbContext(time);
        var (org, tokenId) = SeedOrganization(db, "IR6");
        var oldest = SeedDevice(db, org.Id, tokenId, "OLDEST", collectedHoursAgo: 30);
        var middle = SeedDevice(db, org.Id, tokenId, "MIDDLE", collectedHoursAgo: 28);
        var newest = SeedDevice(db, org.Id, tokenId, "NEWEST", collectedHoursAgo: 26);
        await db.SaveChangesAsync();

        var service = Sweep(db, time);

        (await service.SweepAsync(batchSize: 2)).ShouldBe(2);

        (await ReloadAsync(oldest.Id)).InventoryRequestedAt.ShouldBe(Start);
        (await ReloadAsync(middle.Id)).InventoryRequestedAt.ShouldBe(Start);
        (await ReloadAsync(newest.Id)).InventoryRequestedAt
            .ShouldBeNull("the third device waits for the next batch; the two that have waited longest go first");

        // The sweeper drains while a batch comes back full; the next batch picks
        // up the remainder and nothing is asked twice.
        (await service.SweepAsync(batchSize: 2)).ShouldBe(1);
        (await ReloadAsync(newest.Id)).InventoryRequestedAt.ShouldBe(Start);
    }

    [Fact]
    public async Task A_threshold_of_zero_disables_the_sweep()
    {
        var time = new TestClock(Start);
        await using var db = _fixture.CreateDbContext(time);
        var (org, tokenId) = SeedOrganization(db, "IR7");
        var stale = SeedDevice(db, org.Id, tokenId, "STALE0", collectedHoursAgo: 25);
        await db.SaveChangesAsync();

        (await Sweep(db, time, refreshAfterHours: 0).SweepAsync(500)).ShouldBe(0);

        try
        {
            (await ReloadAsync(stale.Id)).InventoryRequestedAt.ShouldBeNull("0 means the platform never asks on its own");
            (await SweepAuditEntriesAsync(org.Id)).ShouldBeEmpty();
        }
        finally
        {
            // This is the one test that leaves a stale, unrequested device behind,
            // and its siblings sweep the shared database at the same instant with
            // the default threshold and assert exact counts. Take it out of their
            // way whatever order xUnit runs the class in: an administrator's
            // request makes it pending, which is exactly what a sweep skips.
            stale.RequestInventoryRefresh(Start);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Each_organization_in_a_batch_gets_exactly_one_audit_entry_carrying_its_count()
    {
        var time = new TestClock(Start);
        await using var db = _fixture.CreateDbContext(time);
        var (first, firstToken) = SeedOrganization(db, "IR8A");
        var (second, secondToken) = SeedOrganization(db, "IR8B");
        SeedDevice(db, first.Id, firstToken, "A1", collectedHoursAgo: 30);
        SeedDevice(db, first.Id, firstToken, "A2", collectedHoursAgo: 26);
        SeedDevice(db, first.Id, firstToken, "A3", collectedHoursAgo: 2);
        SeedDevice(db, second.Id, secondToken, "B1", collectedHoursAgo: 27);
        await db.SaveChangesAsync();

        (await Sweep(db, time).SweepAsync(500)).ShouldBe(3);

        var entries = await SweepAuditEntriesAsync(first.Id, second.Id);
        entries.Count.ShouldBe(2, "one summary row per organization, not one per device");

        var forFirst = entries.Single(a => a.OrganizationId == first.Id);
        forFirst.ActorType.ShouldBe(AuditActorType.System);
        forFirst.ActorId.ShouldBeNull("no person is behind a scheduled sweep");
        forFirst.ActorDisplay.ShouldBe(InventoryRefreshSweepService.ActorDisplay);
        forFirst.Result.ShouldBe(AuditResult.Success);
        forFirst.OccurredAt.ShouldBe(Start);
        forFirst.PreviousState.ShouldBeNull();
        RequestedCount(forFirst).ShouldBe(2, "the fresh device in the same organization is not counted");
        OlderThanHours(forFirst).ShouldBe(24);

        var forSecond = entries.Single(a => a.OrganizationId == second.Id);
        forSecond.ActorType.ShouldBe(AuditActorType.System);
        RequestedCount(forSecond).ShouldBe(1);
        OlderThanHours(forSecond).ShouldBe(24);
    }

    [Fact]
    public async Task A_second_sweep_immediately_after_finds_nothing()
    {
        var time = new TestClock(Start);
        await using var db = _fixture.CreateDbContext(time);
        var (org, tokenId) = SeedOrganization(db, "IR9");
        var stale = SeedDevice(db, org.Id, tokenId, "ONCE", collectedHoursAgo: 25);
        await db.SaveChangesAsync();

        var service = Sweep(db, time);

        (await service.SweepAsync(500)).ShouldBe(1);
        (await service.SweepAsync(500)).ShouldBe(0, "a device already asked must not be asked again");
        (await SweepAuditEntriesAsync(org.Id)).Count.ShouldBe(1, "an empty sweep leaves no audit row");

        // Even days later the request is still outstanding: the agent has not
        // uploaded, so the flag it will see on its next heartbeat is unchanged
        // and re-stamping it would achieve nothing.
        time.Advance(TimeSpan.FromHours(48));
        // The count is deliberately not asserted here. The sweep is fleet-wide and
        // the database is shared with every other suite in the collection, whose
        // devices legitimately go stale two days on. What this test owns is that
        // THIS outstanding request is not re-stamped.
        await service.SweepAsync(500);
        (await ReloadAsync(stale.Id)).InventoryRequestedAt.ShouldBe(Start);
    }

    private static int RequestedCount(AuditLogEntry entry) => ReadState(entry).GetProperty("requested").GetInt32();

    private static int OlderThanHours(AuditLogEntry entry) => ReadState(entry).GetProperty("olderThanHours").GetInt32();

    private static JsonElement ReadState(AuditLogEntry entry)
    {
        entry.NewState.ShouldNotBeNull("the summary lives in new_state");
        using var document = JsonDocument.Parse(entry.NewState);
        return document.RootElement.Clone();
    }
}
