using EndpointAgent.Windows;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// Windows integration tests: runs the real collector against this machine's WMI
/// and network stack, asserting shape invariants rather than machine-specific
/// values.
/// </summary>
public sealed class WindowsInventoryCollectorTests
{
    private static WindowsInventoryCollector CreateCollector()
    {
        // One instance behind both faces, exactly as the service registers it: the
        // posture collector is also the BitLocker collector, so a single reader of
        // Win32_EncryptableVolume answers both.
        var posture = new WindowsSecurityPostureCollector(
            new WindowsLocalAccountsCollector(NullLogger<WindowsLocalAccountsCollector>.Instance),
            NullLogger<WindowsSecurityPostureCollector>.Instance);

        return new WindowsInventoryCollector(
            new WindowsSystemInfoProvider(NullLogger<WindowsSystemInfoProvider>.Instance),
            new WindowsLocalAccountsCollector(NullLogger<WindowsLocalAccountsCollector>.Instance),
            new WindowsSoftwareCollector(
                NullLogger<WindowsSoftwareCollector>.Instance,
                new WindowsInstallLocationResolver(NullLogger<WindowsInstallLocationResolver>.Instance),
                new WindowsUpgradeCodeIndex(NullLogger<WindowsUpgradeCodeIndex>.Instance)),
            posture,
            new WindowsServiceProcessProvider(NullLogger<WindowsServiceProcessProvider>.Instance),
            new WindowsUpdateCollector(NullLogger<WindowsUpdateCollector>.Instance),
            new WindowsDriverCollector(NullLogger<WindowsDriverCollector>.Instance),
            posture,
            new WindowsChromeCollector(NullLogger<WindowsChromeCollector>.Instance),
            TimeProvider.System,
            NullLogger<WindowsInventoryCollector>.Instance);
    }

    [Fact]
    public async Task Collects_a_complete_report_without_throwing()
    {
        var report = await CreateCollector().CollectAsync(CancellationToken.None);

        report.ShouldNotBeNull();
        report.Hardware.ShouldNotBeNull();
        report.NetworkInterfaces.ShouldNotBeNull();
        report.CollectedAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Reports_plausible_hardware_facts_for_this_machine()
    {
        var hardware = (await CreateCollector().CollectAsync(CancellationToken.None)).Hardware;

        // Any real Windows machine has a CPU, RAM and at least one fixed volume.
        hardware.CpuName.ShouldNotBeNullOrWhiteSpace();
        hardware.CpuLogicalProcessors.ShouldNotBeNull();
        hardware.CpuLogicalProcessors.Value.ShouldBe(Environment.ProcessorCount);
        hardware.TotalMemoryBytes.ShouldNotBeNull();
        hardware.TotalMemoryBytes.Value.ShouldBeGreaterThan(1_000_000_000);
        hardware.Disks.ShouldNotBeEmpty();
        hardware.Disks.All(d => d.SizeBytes > 0).ShouldBeTrue();
        hardware.Disks.All(d => d.FreeBytes <= d.SizeBytes).ShouldBeTrue();
    }

    [Fact]
    public async Task Reports_at_least_one_network_interface_and_no_loopback()
    {
        var interfaces = (await CreateCollector().CollectAsync(CancellationToken.None)).NetworkInterfaces;

        interfaces.ShouldNotBeEmpty();
        interfaces.ShouldAllBe(n => !string.IsNullOrWhiteSpace(n.Name));
        interfaces.ShouldAllBe(n => !n.Name.Contains("Loopback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Interface_addresses_are_parseable_ips()
    {
        var interfaces = (await CreateCollector().CollectAsync(CancellationToken.None)).NetworkInterfaces;

        foreach (var address in interfaces.SelectMany(n => n.IpAddresses))
        {
            System.Net.IPAddress.TryParse(address, out _).ShouldBeTrue(
                $"'{address}' should be a parseable IP address");
        }
    }

    /// <summary>
    /// The inventory snapshot is a summary and stays one: at most sixty
    /// processes, and therefore never near the 500 the server refuses. Pinned
    /// here because the collector now shares its enumeration with Force Stop,
    /// which takes everything; the two must not have been swapped.
    /// </summary>
    [Fact]
    public async Task Carries_at_most_sixty_processes_in_the_snapshot()
    {
        var report = await CreateCollector().CollectAsync(CancellationToken.None);

        report.Processes.ShouldNotBeNull();
        report.Processes!.Count.ShouldBeLessThanOrEqualTo(60);
        report.Processes.ShouldNotBeEmpty();
    }

    /// <summary>
    /// The Chrome section is always present -- "not installed" is an answer, not
    /// an omission -- and its status is one the server knows how to parse.
    /// </summary>
    [Fact]
    public async Task Carries_a_chrome_section_with_a_status_the_server_understands()
    {
        var report = await CreateCollector().CollectAsync(CancellationToken.None);

        var chrome = report.Chrome.ShouldNotBeNull();
        InventoryChrome.Statuses.ShouldContain(chrome.Status);
        chrome.Profiles.ShouldNotBeNull();
    }
}
