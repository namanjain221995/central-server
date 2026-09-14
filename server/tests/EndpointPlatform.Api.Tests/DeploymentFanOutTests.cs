using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Domain.Groups;
using EndpointPlatform.Domain.Tasks;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Fan-out at fleet scale: one request, one deployment, one task per device that
/// needs it.
/// </summary>
/// <remarks>
/// <para>
/// The fleet is ~200 devices heading for 300-350, so a deployment to a group of
/// that size is the realistic worst case and is measured here rather than
/// assumed. The numbers this prints are real for the machine that ran it; they
/// are a regression signal, not a production benchmark.
/// </para>
/// <para>
/// What it is really guarding is shape, not speed: resolution loads the devices
/// and all of their software in a fixed number of queries, so the cost of adding
/// a device is one more row rather than one more round trip. An N+1 here would
/// not fail a small test — it would only surface on the day someone targets the
/// whole estate.
/// </para>
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class DeploymentFanOutTests(AdminApiPostgresFixture fixture, ITestOutputHelper output)
{
    private readonly AdminApiPostgresFixture _fixture = fixture;
    private readonly ITestOutputHelper _output = output;

    private const string ProductCode = "{7F3E2A19-FEED-4C0D-9A11-5B6C7D8E9F00}";

    private sealed record CreateResponse(Guid DeploymentId, int Targeted, int Queued, int Skipped);

    private async Task<Guid> RegisterPackageAsync(HttpClient client, string version)
    {
        var bytes = Encoding.UTF8.GetBytes("msi-" + Guid.CreateVersion7().ToString("N") + new string('z', 300));
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));

        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        var form = new MultipartFormDataContent
        {
            { file, "file", "app.msi" },
            { new StringContent("FanOut App"), "name" },
            { new StringContent(version), "version" },
            { new StringContent("Contoso"), "publisher" },
            { new StringContent(sha), "sha256" },
            { new StringContent(ProductCode), "msiProductCode" },
        };

        (await client.PostAsync(new Uri("/admin/v1/packages/", UriKind.Relative), form))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        await using var db = _fixture.CreateDbContext();
        return await db.SoftwarePackages.Where(p => p.Sha256 == sha).Select(p => p.Id).SingleAsync();
    }

    /// <summary>
    /// Seeds a group of <paramref name="count"/> devices, half of which already
    /// have the package so the eligibility engine has real work to do.
    /// </summary>
    private async Task<Guid> SeedGroupAsync(int count, string installedVersion)
    {
        await using var db = _fixture.CreateDbContext();
        var organizationId = await db.Organizations.Select(o => o.Id).FirstAsync();
        var actorId = await db.PlatformUsers.Select(u => u.Id).FirstAsync();
        var now = DateTimeOffset.UtcNow;

        var token = new EnrollmentToken(
            organizationId, $"fanout-{Guid.CreateVersion7():N}",
            Convert.ToHexStringLower(SHA256.HashData(Guid.CreateVersion7().ToByteArray())),
            actorId, "fan-out-tests", now.AddHours(1), count + 1);
        db.EnrollmentTokens.Add(token);

        var group = new DeviceGroup(
            organizationId, $"Fleet {Guid.CreateVersion7():N}"[..20], "fan-out tests", DeviceGroupType.Static);
        db.DeviceGroups.Add(group);

        for (var i = 0; i < count; i++)
        {
            var device = Device.Enroll(
                organizationId, $"FAN-{i:D4}-{Guid.CreateVersion7():N}"[..14],
                $"smbios-{Guid.CreateVersion7()}", "1.5.0", "Microsoft Windows 11 Pro", token.Id, now);
            db.Devices.Add(device);
            device.MoveToGroup(group.Id);

            // Half the fleet is already current; the rest needs the package. Also
            // gives every device a handful of unrelated applications, so matching
            // has something to sift through rather than one row per device.
            if (i % 2 == 0)
            {
                db.DeviceSoftware.Add(new DeviceSoftware(
                    device.Id, "FanOut App", installedVersion, "Contoso", null, null, "x64", now,
                    "Machine", null, ProductCode));
            }

            for (var j = 0; j < 5; j++)
            {
                db.DeviceSoftware.Add(new DeviceSoftware(
                    device.Id, $"Filler {j}", "1.0.0", "Other", null, null, "x64", now,
                    "Machine", null, null));
            }
        }

        await db.SaveChangesAsync();
        return group.Id;
    }

    [Theory]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(200)]
    [InlineData(350)]
    public async Task A_group_deployment_creates_one_task_per_device_that_needs_it(int deviceCount)
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var packageId = await RegisterPackageAsync(client, "5.0.0");
        var groupId = await SeedGroupAsync(deviceCount, installedVersion: "5.0.0");

        var stopwatch = Stopwatch.StartNew();
        var response = await client.PostAsJsonAsync(
            new Uri("/admin/v1/deployments", UriKind.Relative),
            new { packageId, deviceIds = Array.Empty<Guid>(), groupIds = new[] { groupId } });
        stopwatch.Stop();

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var result = (await response.Content.ReadFromJsonAsync<CreateResponse>())!;

        var expectedCurrent = (deviceCount + 1) / 2;   // i % 2 == 0
        var expectedNeeding = deviceCount - expectedCurrent;

        result.Targeted.ShouldBe(deviceCount);
        result.Queued.ShouldBe(expectedNeeding);
        result.Skipped.ShouldBe(expectedCurrent);

        // The saving the eligibility engine exists for, stated as a number.
        _output.WriteLine(
            $"devices={deviceCount} queued={result.Queued} skipped={result.Skipped} "
            + $"elapsed={stopwatch.ElapsedMilliseconds}ms");

        await using var db = _fixture.CreateDbContext();

        var targets = await db.SoftwareDeploymentTargets
            .CountAsync(t => t.DeploymentId == result.DeploymentId);
        targets.ShouldBe(deviceCount, "every targeted device is recorded, skipped ones included");

        // No device gets two installs, whatever the fan-out size.
        var duplicated = await db.SoftwareDeploymentTargets
            .Where(t => t.DeploymentId == result.DeploymentId)
            .GroupBy(t => t.DeviceId)
            .AnyAsync(g => g.Count() > 1);
        duplicated.ShouldBeFalse();

        var tasks = await db.DeviceTasks
            .CountAsync(t => t.Type == DeviceTaskType.InstallPackage
                && db.SoftwareDeploymentTargets
                    .Where(x => x.DeploymentId == result.DeploymentId)
                    .Select(x => x.TaskId)
                    .Contains(t.Id));
        tasks.ShouldBe(expectedNeeding);
    }

    /// <summary>
    /// Reading a deployment's results is one query for the targets and one for
    /// the tally, not one per device.
    /// </summary>
    [Fact]
    public async Task Reading_a_large_deployment_does_not_degrade_per_device()
    {
        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.SuperAdminEmail);
        using var client = _fixture.CreateClientFor(token);

        var packageId = await RegisterPackageAsync(client, "6.0.0");
        var groupId = await SeedGroupAsync(200, installedVersion: "1.0.0");

        var created = (await (await client.PostAsJsonAsync(
            new Uri("/admin/v1/deployments", UriKind.Relative),
            new { packageId, deviceIds = Array.Empty<Guid>(), groupIds = new[] { groupId } }))
            .Content.ReadFromJsonAsync<CreateResponse>())!;

        var stopwatch = Stopwatch.StartNew();
        var detail = await client.GetAsync(
            new Uri($"/admin/v1/deployments/{created.DeploymentId}", UriKind.Relative));
        stopwatch.Stop();

        detail.StatusCode.ShouldBe(HttpStatusCode.OK);
        _output.WriteLine($"detail read for 200 devices: {stopwatch.ElapsedMilliseconds}ms");

        var list = Stopwatch.StartNew();
        (await client.GetAsync(new Uri("/admin/v1/deployments", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        list.Stop();

        _output.WriteLine($"deployment list read: {list.ElapsedMilliseconds}ms");
    }
}
