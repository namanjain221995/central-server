using System.Text.Json;
using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Chrome;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EndpointPlatform.Infrastructure.Devices;

/// <summary>
/// Ingests inventory uploads from authenticated agents.
/// </summary>
/// <remarks>
/// <para>
/// Input is agent-reported and therefore untrusted: lengths and ranges are
/// validated (the endpoint pre-validates shape, the domain re-validates on
/// construction), collection sizes are capped, and free-text values are stored
/// verbatim but never interpreted. A hostile agent can lie about its own facts —
/// that is inherent — but it must not be able to damage the platform or another
/// device's record.
/// </para>
/// <para>
/// The snapshot replaces the previous one atomically: hardware row upserted,
/// network interface rows deleted and re-inserted, device fields updated, all in
/// the one SaveChanges.
/// </para>
/// </remarks>
public sealed class DeviceInventoryService(
    EndpointPlatformDbContext dbContext,
    TimeProvider timeProvider,
    AuditWriter auditWriter,
    ILogger<DeviceInventoryService> logger)
{
    /// <summary>Caps that no legitimate machine exceeds; anything above is a hostile payload.</summary>
    /// <summary>
    /// How far above a section's limit a report may go before it is refused
    /// outright instead of truncated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every <c>Max*</c> below is a TRUNCATION limit: this service takes that many
    /// entries and ignores the rest, so a machine that legitimately reports more
    /// than expected still gets a usable inventory. The Agent API additionally
    /// refused any report that exceeded one of these counts, which defeated that
    /// design completely -- a single oversized section discarded the WHOLE report,
    /// including BitLocker state, local accounts and drivers, and the agent then
    /// retried forever.
    /// </para>
    /// <para>
    /// That is not theoretical: a developer laptop running VMware, VirtualBox,
    /// Hyper-V and a VPN client enumerates 81 network interfaces against a limit
    /// of 64, and reported no inventory at all until this was fixed.
    /// </para>
    /// <para>
    /// The counts are still bounded, because an unbounded list from a compromised
    /// agent is a denial-of-service vector. This multiplier is the line between
    /// "a big machine" (truncate, keep the rest) and "not a plausible machine"
    /// (refuse). It is deliberately generous: being wrong in the refusing
    /// direction costs an entire estate's inventory.
    /// </para>
    /// </remarks>
    public const int OversizeRefusalMultiplier = 16;

    public const int MaxDisks = 64;
    public const int MaxNetworkInterfaces = 64;
    public const int MaxIpAddressesPerInterface = 32;
    public const int MaxLocalUsers = 2048;
    public const int MaxLocalGroups = 512;
    public const int MaxGroupMembers = 2048;
    public const int MaxSoftwareEntries = 8192;
    public const int MaxServices = 2048;
    public const int MaxProcesses = 500;
    public const int MaxUpdateHistory = 200;
    public const int MaxDrivers = 4096;
    public const int MaxBitLockerVolumes = 64;

    /// <summary>
    /// Chrome caps, pinned to the wire contract rather than restated: the agent
    /// clamps to the contract's values before sending, so the two cannot drift.
    /// </summary>
    public const int MaxChromeProfiles = InventoryChrome.MaxProfiles;
    public const int MaxChromeExtensionsPerProfile = InventoryChromeProfile.MaxExtensions;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly AuditWriter _auditWriter = auditWriter
        ?? throw new ArgumentNullException(nameof(auditWriter));

    private readonly ILogger<DeviceInventoryService> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    public async Task ApplyAsync(Device device, InventoryReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(report);

        var now = _timeProvider.GetUtcNow();

        var hardware = await _dbContext.DeviceHardware
            .SingleOrDefaultAsync(h => h.DeviceId == device.Id, cancellationToken);

        if (hardware is null)
        {
            hardware = new DeviceHardware(device.Id);
            _dbContext.DeviceHardware.Add(hardware);
        }

        var disks = (report.Hardware.Disks ?? []).Take(MaxDisks)
            .Select(d => new
            {
                name = Truncate(d.Name, 64),
                fileSystem = Truncate(d.FileSystem, 32),
                sizeBytes = Math.Max(0, d.SizeBytes),
                freeBytes = Math.Max(0, d.FreeBytes),
            })
            .ToArray();

        hardware.Apply(
            report.Hardware.SerialNumber,
            report.Hardware.Manufacturer,
            report.Hardware.Model,
            report.Hardware.CpuName,
            report.Hardware.CpuPhysicalCores,
            report.Hardware.CpuLogicalProcessors,
            report.Hardware.TotalMemoryBytes,
            JsonSerializer.Serialize(disks, JsonOptions),
            now);

        // Replace the interface set wholesale.
        var existingInterfaces = await _dbContext.DeviceNetworkInterfaces
            .Where(n => n.DeviceId == device.Id)
            .ToListAsync(cancellationToken);

        _dbContext.DeviceNetworkInterfaces.RemoveRange(existingInterfaces);

        foreach (var reported in (report.NetworkInterfaces ?? []).Take(MaxNetworkInterfaces))
        {
            var addresses = (reported.IpAddresses ?? [])
                .Take(MaxIpAddressesPerInterface)
                .Select(ip => Truncate(ip, 64))
                .ToArray();

            _dbContext.DeviceNetworkInterfaces.Add(new DeviceNetworkInterface(
                device.Id,
                reported.Name,
                reported.MacAddress,
                JsonSerializer.Serialize(addresses, JsonOptions),
                reported.IsUp,
                now));
        }

        if (report.LocalAccounts is { } localAccounts)
        {
            await ApplyLocalAccountsAsync(device, localAccounts, now, cancellationToken);
        }

        if (report.Software is { } software)
        {
            await ApplySoftwareAsync(device, software, now, cancellationToken);
        }

        if (report.SecurityPosture is { } posture)
        {
            await ApplySecurityPostureAsync(device, posture, now, cancellationToken);
        }

        if (report.Services is { } || report.Processes is { })
        {
            await ApplyServicesProcessesAsync(device, report.Services, report.Processes, now, cancellationToken);
        }

        if (report.WindowsUpdate is { } windowsUpdate)
        {
            await ApplyWindowsUpdateAsync(device, windowsUpdate, now, cancellationToken);
        }

        if (report.Drivers is { } drivers)
        {
            await ApplyDriversAsync(device, drivers, now, cancellationToken);
        }

        if (report.BitLocker is { } bitLocker)
        {
            await ApplyBitLockerAsync(device, bitLocker, now, cancellationToken);
        }

        if (report.Chrome is { } chrome)
        {
            await ApplyChromeAsync(device, chrome, now, cancellationToken);
        }

        device.RecordInventory(report.LoggedOnUser, now);

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Inventory applied for device {DeviceId} ({Hostname}): {InterfaceCount} interface(s), "
            + "{DiskCount} disk(s).",
            device.Id,
            device.Hostname,
            Math.Min((report.NetworkInterfaces ?? []).Count, MaxNetworkInterfaces),
            disks.Length);
    }

    private static List<LocalAccountView> ToAccountViews(IEnumerable<DeviceLocalUser> users) =>
        users
            .Select(u => new LocalAccountView(u.Sid, u.Name, u.Enabled, u.IsLocalAdministrator))
            .ToList();

    /// <summary>
    /// Records a change in local-administrator posture, and only a change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not an event per evaluation. The verdict is derived on
    /// read, so evaluating it is not a mutation, and an endpoint reporting its
    /// inventory every few minutes would otherwise write thousands of identical
    /// rows into an append-only store — burying the transitions that matter in
    /// the ones that do not.
    /// </para>
    /// <para>
    /// The transition is the security event: a machine that was standard-user
    /// and now has an interactive administrator is worth an alert, and so is the
    /// reverse, because somebody changed something. Attributed to the agent,
    /// because no operator performed this — the endpoint reported a new fact.
    /// </para>
    /// </remarks>
    private void AuditPostureTransition(
        Device device, LocalAdminPostureResult before, LocalAdminPostureResult after)
    {
        if (before.Compliance == after.Compliance)
        {
            return;
        }

        // Unknown -> anything on the first report is not a change in the
        // machine, only the arrival of evidence about it. Recording that as a
        // posture change would make every enrollment look like a security
        // transition.
        if (before.Compliance == LocalAdminCompliance.Unknown)
        {
            return;
        }

        _auditWriter.Stage(
            device.OrganizationId,
            AuditActorType.Agent,
            device.Id,
            device.Hostname,
            action: "localuser.posture.changed",
            after.Compliance == LocalAdminCompliance.NonCompliant
                ? AuditResult.Failure
                : AuditResult.Success,
            audit => audit
                .OnDevice(device.Id, device.Hostname)
                .OnTarget("device", device.Id.ToString(), device.Hostname)
                .WithStateChange(
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        compliance = before.Compliance.ToString(),
                        interactiveAdministrators = before.InteractiveAdministrators
                            .Select(a => a.Username).ToList(),
                    }),
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        compliance = after.Compliance.ToString(),
                        interactiveAdministrators = after.InteractiveAdministrators
                            .Select(a => a.Username).ToList(),
                    })));

        _logger.LogInformation(
            "Local administrator posture on {Hostname} changed from {Before} to {After}.",
            device.Hostname, before.Compliance, after.Compliance);
    }

    private async Task ApplyLocalAccountsAsync(
        Device device,
        InventoryLocalAccounts localAccounts,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Replace-wholesale, same pattern as network interfaces.
        var existingUsers = await _dbContext.DeviceLocalUsers
            .Where(u => u.DeviceId == device.Id)
            .ToListAsync(cancellationToken);

        // The verdict this report replaces, computed before the rows go. Both
        // sets are already in hand here, which is the one place a change in
        // posture can be noticed without storing a cached verdict to compare
        // against.
        var postureBefore = LocalAdministratorPosture.Evaluate(ToAccountViews(existingUsers));

        _dbContext.DeviceLocalUsers.RemoveRange(existingUsers);

        var reportedUsers = new List<DeviceLocalUser>();

        foreach (var user in (localAccounts.Users ?? []).Take(MaxLocalUsers))
        {
            reportedUsers.Add(new DeviceLocalUser(
                device.Id,
                user.Sid,
                user.Name,
                user.FullName,
                user.Description,
                user.Enabled,
                user.PasswordRequired,
                user.PasswordExpires,
                user.LastLogon,
                user.IsLocalAdministrator,
                now));
        }

        _dbContext.DeviceLocalUsers.AddRange(reportedUsers);
        AuditPostureTransition(device, postureBefore, LocalAdministratorPosture.Evaluate(
            ToAccountViews(reportedUsers)));

        var existingGroups = await _dbContext.DeviceLocalGroups
            .Where(g => g.DeviceId == device.Id)
            .ToListAsync(cancellationToken);
        _dbContext.DeviceLocalGroups.RemoveRange(existingGroups);

        foreach (var group in (localAccounts.Groups ?? []).Take(MaxLocalGroups))
        {
            var members = (group.Members ?? []).Take(MaxGroupMembers)
                .Select(m => new
                {
                    name = Truncate(m.Name, 256),
                    sid = Truncate(m.Sid, 184),
                    memberType = Truncate(m.MemberType, 16),
                })
                .ToArray();

            _dbContext.DeviceLocalGroups.Add(new DeviceLocalGroup(
                device.Id,
                group.Sid,
                group.Name,
                group.Description,
                JsonSerializer.Serialize(members, JsonOptions),
                members.Length,
                now));
        }
    }

    /// <summary>
    /// Replaces the device's driver snapshot and audits any change in its driver
    /// health verdict.
    /// </summary>
    /// <remarks>
    /// Whole-snapshot replace, like software and services: the agent sends what the
    /// machine has now, and the server does not try to reason about what changed at
    /// the row level. What it does reason about is the <em>verdict</em>, because a
    /// machine that has just developed a driver fault is worth telling somebody
    /// about, and a row-by-row diff would bury that in noise.
    /// </remarks>
    /// <summary>
    /// Replaces the device's BitLocker snapshot: the availability verdict and every
    /// reported volume.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The availability row is written even when no volumes came with it, and that is
    /// the point of storing it separately. An agent that was refused the WMI query
    /// sends <c>AccessDenied</c> and an empty list; without the row, that would be
    /// indistinguishable from a machine with nothing encryptable and the estate would
    /// appear to have decrypted itself.
    /// </para>
    /// <para>
    /// Volumes are only cleared when the endpoint could actually answer. A failed
    /// query must not delete the last known good picture and leave a console showing
    /// nothing at all.
    /// </para>
    /// </remarks>
    private async Task ApplyBitLockerAsync(
        Device device,
        InventoryBitLocker bitLocker,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var availability = ParseAvailability(bitLocker.Status);

        var status = await _dbContext.DeviceBitLockerStatus
            .SingleOrDefaultAsync(s => s.DeviceId == device.Id, cancellationToken);

        if (status is null)
        {
            status = new DeviceBitLockerStatus(device.Id);
            _dbContext.DeviceBitLockerStatus.Add(status);
        }

        status.Apply(availability, now);

        // A query that did not succeed carries no information about volumes, so the
        // stored snapshot is left alone rather than being emptied.
        if (availability != BitLockerAvailability.Available)
        {
            return;
        }

        var existing = await _dbContext.DeviceBitLockerVolumes
            .Where(v => v.DeviceId == device.Id)
            .ToListAsync(cancellationToken);

        _dbContext.DeviceBitLockerVolumes.RemoveRange(existing);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var volume in (bitLocker.Volumes ?? []).Take(MaxBitLockerVolumes))
        {
            if (string.IsNullOrWhiteSpace(volume.DeviceIdentifier) || !seen.Add(volume.DeviceIdentifier))
            {
                continue;
            }

            _dbContext.DeviceBitLockerVolumes.Add(new DeviceBitLockerVolume(
                device.Id,
                Truncate(volume.DeviceIdentifier, 256)!,
                Truncate(volume.DriveLetter, 8),
                Truncate(volume.PersistentVolumeId, 128),
                volume.VolumeType,
                volume.ConversionStatus,
                volume.ProtectionStatus,
                volume.EncryptionPercentage,
                volume.EncryptionMethod,
                volume.HasRecoveryPasswordProtector,
                JoinProtectorIds(volume.RecoveryProtectorIds),
                now,
                // Startup protectors, kept in their own columns. JoinProtectorIds is
                // shared formatting only -- the three id lists never merge, so a
                // startup protector cannot reach the automatic-escrow target list,
                // which reads RecoveryProtectorIds and nothing else.
                volume.HasTpmProtector,
                JoinProtectorIds(volume.TpmProtectorIds),
                volume.HasTpmPinProtector,
                JoinProtectorIds(volume.TpmPinProtectorIds)));
        }
    }

    /// <summary>
    /// Maps the reported availability string onto the enum, defaulting to
    /// <see cref="BitLockerAvailability.Unknown"/>.
    /// </summary>
    /// <remarks>
    /// An unrecognised value is Unknown and never Available. Anything else would let
    /// a malformed or hostile report have its volume list trusted -- or, worse, have
    /// an absent list read as an unencrypted machine.
    /// </remarks>
    private static BitLockerAvailability ParseAvailability(string? status) =>
        Enum.TryParse<BitLockerAvailability>(status, ignoreCase: false, out var parsed)
        && parsed != BitLockerAvailability.Unknown
            ? parsed
            : BitLockerAvailability.Unknown;

    /// <summary>
    /// Joins protector identifiers for storage, keeping only well-formed GUIDs.
    /// </summary>
    /// <remarks>
    /// The GUID filter is the point. A protector id is a GUID and nothing else, so
    /// rejecting anything that is not one means the column cannot be used to smuggle
    /// arbitrary text -- a recovery key among it -- into the database through a
    /// field an operator will read.
    /// </remarks>
    private static string? JoinProtectorIds(IReadOnlyList<string>? ids)
    {
        if (ids is null || ids.Count == 0)
        {
            return null;
        }

        var wellFormed = ids
            .Where(id => Guid.TryParse(id?.Trim().Trim('{', '}'), out _))
            .Select(id => id.Trim())
            .Take(16)
            .ToArray();

        return wellFormed.Length == 0 ? null : string.Join(',', wellFormed);
    }

    /// <summary>
    /// Upserts the device's Chrome installation row and, when the endpoint's
    /// enumeration was complete, replaces its profiles and extensions wholesale.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The installation row is applied whatever the status says, for the reason the
    /// BitLocker availability row is: a device with a <c>NotInstalled</c> row has
    /// reported and said no, a device with no row has never reported, and the
    /// console has to tell those two apart.
    /// </para>
    /// <para>
    /// Profiles and extensions are replaced only when the status is not
    /// <see cref="ChromeReportStatus.Error"/>. Error means the agent's enumeration
    /// was cut short and the list carries whatever it managed to read; letting that
    /// fragment replace a complete snapshot would make extensions vanish from the
    /// console because a profile directory was briefly locked. Same keep-last-known
    /// rule as BitLocker volumes. <c>NotInstalled</c> is a complete answer and
    /// replaces the profiles with whatever was sent: Chrome's uninstaller leaves
    /// <c>User Data</c> behind, so leftover profiles are still fact, and an empty
    /// list means that data is gone too.
    /// </para>
    /// <para>
    /// Not audited. Inventory is re-reported every cycle, and a row per upload
    /// would bury the events an auditor is looking for under thousands of
    /// identical ones.
    /// </para>
    /// </remarks>
    private async Task ApplyChromeAsync(
        Device device,
        InventoryChrome chrome,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var status = ParseChromeStatus(chrome.Status);

        var installation = await _dbContext.ChromeInstallations
            .SingleOrDefaultAsync(i => i.DeviceId == device.Id, cancellationToken);

        if (installation is null)
        {
            installation = new ChromeInstallation(device.Id);
            _dbContext.ChromeInstallations.Add(installation);
        }

        // Truncated to the contract limits before Apply, which throws over them.
        // The endpoint already refused anything longer, so this is the second
        // fence rather than the first; it exists so that a value which somehow
        // gets past the first cannot fail the whole upload.
        var reported = chrome.Installation;

        installation.Apply(
            status,
            Truncate(reported?.Version, InventoryChromeInstallation.MaxVersion),
            Truncate(reported?.ExecutablePath, InventoryChromeInstallation.MaxExecutablePath),
            Truncate(reported?.Architecture, InventoryChromeInstallation.MaxArchitecture),
            Truncate(reported?.Channel, InventoryChromeInstallation.MaxChannel),
            Truncate(reported?.InstallationScope, InventoryChromeInstallation.MaxInstallationScope),
            Truncate(reported?.InstalledForUser, InventoryChromeInstallation.MaxInstalledForUser),
            Truncate(reported?.UpdaterVersion, InventoryChromeInstallation.MaxUpdaterVersion),
            // Every agent-supplied instant is normalised to UTC before it reaches
            // the entity: timestamptz takes only offset zero and Npgsql refuses any
            // other, which would fail the whole upload -- every section, on every
            // retry -- over a different representation of the same instant.
            reported?.LastUpdateCheck?.ToUniversalTime(),
            now);

        // An incomplete enumeration says nothing about what it did not reach, so
        // the stored profiles stay as last known rather than being replaced by a
        // fragment -- the BitLocker rule for a query that did not succeed.
        if (status == ChromeReportStatus.Error)
        {
            return;
        }

        var existingExtensions = await _dbContext.ChromeExtensions
            .Where(e => e.DeviceId == device.Id)
            .ToListAsync(cancellationToken);
        _dbContext.ChromeExtensions.RemoveRange(existingExtensions);

        var existingProfiles = await _dbContext.ChromeProfiles
            .Where(p => p.DeviceId == device.Id)
            .ToListAsync(cancellationToken);
        _dbContext.ChromeProfiles.RemoveRange(existingProfiles);

        // (SID, profile directory) is the profile's identity, so a repeat is a
        // malformed payload rather than two profiles. Case-insensitive because
        // NTFS is: "Default" and "default" are one directory.
        var seenProfiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in chrome.Profiles ?? [])
        {
            // The cap counts profiles kept, not entries read, so a duplicate can
            // never take the slot of a real profile further down the list. The
            // endpoint has already refused anything past the refusal ceiling, so
            // the loop itself is bounded.
            if (seenProfiles.Count >= MaxChromeProfiles)
            {
                break;
            }

            if (profile is null)
            {
                continue;
            }

            var userSid = Truncate(profile.UserSid, InventoryChromeProfile.MaxUserSid);
            var profileKey = Truncate(profile.ProfileKey, InventoryChromeProfile.MaxProfileKey);
            var profilePath = Truncate(profile.ProfilePath, InventoryChromeProfile.MaxProfilePath);

            if (userSid is null || profileKey is null || profilePath is null
                || !seenProfiles.Add($"{userSid}\n{profileKey}"))
            {
                continue;
            }

            var row = new ChromeProfile(
                device.Id,
                installation.Id,
                userSid,
                Truncate(profile.UserAccount, InventoryChromeProfile.MaxUserAccount),
                profileKey,
                Truncate(profile.ProfileName, InventoryChromeProfile.MaxProfileName),
                profilePath,
                profile.IsManaged,
                profile.LastActiveAt?.ToUniversalTime(),
                now);
            _dbContext.ChromeProfiles.Add(row);

            // The id is the extension's identity within a profile; first wins,
            // as for every other duplicate in an inventory upload.
            var seenExtensions = new HashSet<string>(StringComparer.Ordinal);

            foreach (var extension in profile.Extensions ?? [])
            {
                if (seenExtensions.Count >= MaxChromeExtensionsPerProfile)
                {
                    break; // Kept, not read: the same rule as the profile cap.
                }

                // The id is the one value the fleet-wide extension index is keyed
                // on, and the constructor refuses a malformed one. Skipping it
                // here keeps one bad entry from failing the whole upload.
                if (extension is null
                    || !ChromeExtension.IsValidExtensionId(extension.ExtensionId)
                    || !seenExtensions.Add(extension.ExtensionId))
                {
                    continue;
                }

                _dbContext.ChromeExtensions.Add(new ChromeExtension(
                    device.Id,
                    row.Id,
                    extension.ExtensionId,
                    Truncate(extension.Name, InventoryChromeExtension.MaxName),
                    Truncate(extension.Version, InventoryChromeExtension.MaxVersion),
                    // Chrome has shipped manifest versions 1 to 3; anything outside
                    // 1..99 did not come from Chrome, and unknown is the honest
                    // value for a number we do not believe.
                    extension.ManifestVersion is >= 1 and <= 99 ? extension.ManifestVersion : null,
                    extension.Enabled,
                    ParseInstallType(extension.InstallType),
                    extension.IsManaged,
                    extension.FromWebStore,
                    Truncate(extension.UpdateUrl, InventoryChromeExtension.MaxUpdateUrl),
                    extension.InstalledAt?.ToUniversalTime(),
                    extension.UpdatedAt?.ToUniversalTime(),
                    now));
            }
        }
    }

    /// <summary>
    /// Maps the reported Chrome status onto the enum, defaulting to
    /// <see cref="ChromeReportStatus.Error"/>.
    /// </summary>
    /// <remarks>
    /// The endpoint refuses anything outside the contract's set, so this only
    /// matters for a value that somehow gets past it. Error is the safe default for
    /// the reason Unknown is for BitLocker: it is the one status that leaves the
    /// last known profiles alone, so a value we cannot read never empties a
    /// device's record and never has an unverified list trusted.
    /// </remarks>
    private static ChromeReportStatus ParseChromeStatus(string? status) =>
        status is not null
        && InventoryChrome.Statuses.Contains(status)
        && Enum.TryParse<ChromeReportStatus>(status, ignoreCase: false, out var parsed)
            ? parsed
            : ChromeReportStatus.Error;

    /// <summary>
    /// Maps Chrome's install-type name onto the enum, defaulting to
    /// <see cref="ChromeExtensionInstallType.Unknown"/>.
    /// </summary>
    /// <remarks>
    /// Checked against the contract's name set before parsing, because
    /// <c>Enum.TryParse</c> also accepts a numeric string and a payload must not
    /// be able to pick an install type by number.
    /// </remarks>
    private static ChromeExtensionInstallType ParseInstallType(string? installType) =>
        installType is not null
        && InventoryChromeExtension.InstallTypes.Contains(installType)
        && Enum.TryParse<ChromeExtensionInstallType>(installType, ignoreCase: false, out var parsed)
            ? parsed
            : ChromeExtensionInstallType.Unknown;

    private async Task ApplyDriversAsync(
        Device device,
        IReadOnlyList<InventoryDriver> drivers,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = await _dbContext.DeviceDrivers
            .Where(d => d.DeviceId == device.Id)
            .ToListAsync(cancellationToken);

        var healthBefore = DriverHealthSummary.Evaluate(existing.Select(d => d.ToView()).ToList());

        _dbContext.DeviceDrivers.RemoveRange(existing);

        var reported = new List<DeviceDriver>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var driver in drivers.Take(MaxDrivers))
        {
            if (string.IsNullOrWhiteSpace(driver.InstanceId))
            {
                continue;
            }

            // The instance id is the devnode's identity, so a repeat is a
            // malformed payload rather than two devices. Keeping the first and
            // dropping the rest stops a duplicate-stuffed upload from inflating
            // the fault counts.
            if (!seen.Add(driver.InstanceId))
            {
                continue;
            }

            reported.Add(new DeviceDriver(
                device.Id,
                Truncate(driver.InstanceId, 512)!,
                Truncate(string.IsNullOrWhiteSpace(driver.DeviceName)
                    ? driver.InstanceId
                    : driver.DeviceName, 384)!,
                Truncate(driver.DeviceClass, 128),
                Truncate(driver.Manufacturer, 256),
                Truncate(driver.DriverProvider, 256),
                Truncate(driver.DriverVersion, 64),
                driver.DriverDate,
                Truncate(driver.InfName, 256),
                NormalizeProblemCode(driver.ProblemCode),
                driver.IsSigned,
                now));
        }

        _dbContext.DeviceDrivers.AddRange(reported);

        AuditDriverHealthTransition(
            device, healthBefore, DriverHealthSummary.Evaluate(reported.Select(d => d.ToView()).ToList()));
    }

    /// <summary>
    /// Keeps an implausible problem code out of the store as "unknown".
    /// </summary>
    /// <remarks>
    /// CM_PROB_* values are small positive integers. A negative or absurd value did
    /// not come from Windows, and storing it would let a hostile agent invent
    /// problem codes -- harmless today, but the classifier would report them as
    /// unattributed problems and skew every fleet count. Unknown is the honest
    /// place for a value we do not believe.
    /// </remarks>
    private static int? NormalizeProblemCode(int? problemCode) =>
        problemCode is >= 0 and <= 1000 ? problemCode : null;

    /// <summary>
    /// Audits a change in an endpoint's driver health verdict.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Transition-only, for the same reason the local-administrator posture audit is
    /// (see <see cref="AuditPostureTransition"/>): drivers are re-reported on every
    /// inventory cycle, and writing a row each time would bury the transitions that
    /// matter under thousands that do not.
    /// </para>
    /// <para>
    /// Unknown to anything is not audited. A machine reporting drivers for the first
    /// time has not changed -- evidence about it has merely arrived -- and recording
    /// that as a fault would make every enrollment look like a new problem.
    /// </para>
    /// </remarks>
    private void AuditDriverHealthTransition(
        Device device, DriverHealthResult before, DriverHealthResult after)
    {
        if (before.OverallState == DriverHealthState.Unknown)
        {
            return;
        }

        // The fault set, not just the overall state: a machine that swaps one
        // faulted device for a different one stays "Problem" overall while
        // something material has changed.
        var beforeFaults = before.Faults.Select(f => f.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var afterFaults = after.Faults.Select(f => f.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (beforeFaults.SetEquals(afterFaults))
        {
            return;
        }

        _auditWriter.Stage(
            device.OrganizationId,
            AuditActorType.Agent,
            device.Id,
            device.Hostname,
            action: "driver.problem.detected",
            after.Faults.Count > before.Faults.Count ? AuditResult.Failure : AuditResult.Success,
            audit => audit
                .OnDevice(device.Id, device.Hostname)
                .OnTarget("device", device.Id.ToString(), device.Hostname)
                .WithStateChange(
                    JsonSerializer.Serialize(DescribeHealth(before), JsonOptions),
                    JsonSerializer.Serialize(DescribeHealth(after), JsonOptions)));

        _logger.LogInformation(
            "Driver health on {Hostname} changed: {BeforeCount} fault(s) -> {AfterCount} fault(s).",
            device.Hostname, before.Faults.Count, after.Faults.Count);
    }

    private static object DescribeHealth(DriverHealthResult health) => new
    {
        state = health.OverallState.ToString(),
        driverFaults = health.DriverFaultCount,
        deviceFaults = health.DeviceFaultCount,
        indeterminateFaults = health.IndeterminateFaultCount,
        unknown = health.UnknownCount,
        faults = health.Faults
            .Select(f => new
            {
                instanceId = f.InstanceId,
                deviceName = f.DeviceName,
                problemCode = f.Verdict.ProblemCode,
                fault = f.Verdict.FaultKind.ToString(),
            })
            .ToList(),
    };

    private async Task ApplySoftwareAsync(
        Device device,
        IReadOnlyList<InventorySoftware> software,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = await _dbContext.DeviceSoftware
            .Where(s => s.DeviceId == device.Id)
            .ToListAsync(cancellationToken);
        // The evidence rows go with their software rows: the database cascades
        // the delete, and nothing here has to know how many there were.
        _dbContext.DeviceSoftware.RemoveRange(existing);

        foreach (var app in software.Take(MaxSoftwareEntries))
        {
            if (string.IsNullOrWhiteSpace(app.Name))
            {
                continue;
            }

            var row = new DeviceSoftware(
                device.Id,
                Truncate(app.Name, 384)!,
                Truncate(app.Version, 128),
                Truncate(app.Publisher, 256),
                Truncate(app.InstallDate, 32),
                Truncate(app.InstallLocation, 512),
                Truncate(app.Architecture, 16),
                now,
                Truncate(app.InstallationScope, 16),
                Truncate(app.InstalledForUser, 256),
                Truncate(app.ProductCode, 64),
                Truncate(app.IdentityKind, InventorySoftware.MaxIdentityKind),
                Truncate(app.StableKey, InventorySoftware.MaxStableKey),
                Truncate(app.VersionKey, InventorySoftware.MaxVersionKey),
                Truncate(app.Confidence, InventorySoftware.MaxConfidence),
                Truncate(app.Category, InventorySoftware.MaxCategory),
                Truncate(app.PackageFamilyName, InventorySoftware.MaxPackageName),
                Truncate(app.PackageFullName, InventorySoftware.MaxPackageName),
                Truncate(app.UpgradeCode, InventorySoftware.MaxUpgradeCode),
                Truncate(app.ExecutablePath, InventorySoftware.MaxExecutablePath),
                Truncate(app.SignerSubject, InventorySoftware.MaxSignerSubject),
                Truncate(app.SignatureStatus, InventorySoftware.MaxSignatureStatus));
            _dbContext.DeviceSoftware.Add(row);

            var ordinal = 0;
            foreach (var witness in (app.Evidence ?? []).Take(InventorySoftware.MaxEvidence))
            {
                if (witness is null || string.IsNullOrWhiteSpace(witness.Source))
                {
                    continue;
                }

                _dbContext.DeviceSoftwareEvidence.Add(new DeviceSoftwareEvidence(
                    row.Id,
                    ordinal++,
                    Truncate(witness.Source, InventorySoftwareEvidence.MaxSource)!,
                    Truncate(witness.Name, InventorySoftwareEvidence.MaxName),
                    Truncate(witness.Detail, InventorySoftwareEvidence.MaxDetail)));
            }
        }
    }

    private async Task ApplySecurityPostureAsync(
        Device device,
        InventorySecurityPosture posture,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var row = await _dbContext.DeviceSecurityPosture
            .SingleOrDefaultAsync(p => p.DeviceId == device.Id, cancellationToken);

        if (row is null)
        {
            row = new DeviceSecurityPosture(device.Id);
            _dbContext.DeviceSecurityPosture.Add(row);
        }

        row.Apply(
            posture.DefenderAntivirusEnabled, posture.DefenderRealtimeProtectionEnabled, posture.DefenderSignatureAgeDays,
            posture.FirewallDomainEnabled, posture.FirewallPrivateEnabled, posture.FirewallPublicEnabled,
            posture.SecureBootEnabled, posture.TpmPresent, posture.TpmEnabled, posture.TpmSpecVersion,
            posture.BitLockerSystemDriveStatus, posture.LocalAdministratorCount, now);
    }

    private async Task ApplyServicesProcessesAsync(
        Device device,
        IReadOnlyList<InventoryService>? services,
        IReadOnlyList<InventoryProcess>? processes,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (services is not null)
        {
            var existing = await _dbContext.DeviceServices
                .Where(s => s.DeviceId == device.Id).ToListAsync(cancellationToken);
            _dbContext.DeviceServices.RemoveRange(existing);

            foreach (var svc in services.Take(MaxServices))
            {
                if (string.IsNullOrWhiteSpace(svc.Name) || string.IsNullOrWhiteSpace(svc.DisplayName))
                {
                    continue;
                }

                _dbContext.DeviceServices.Add(new DeviceServiceEntry(
                    device.Id, Truncate(svc.Name, 256)!, Truncate(svc.DisplayName, 384)!,
                    Truncate(svc.Status, 32) ?? "Unknown", Truncate(svc.StartMode, 32) ?? "Unknown", now));
            }
        }

        if (processes is not null)
        {
            var existing = await _dbContext.DeviceProcesses
                .Where(p => p.DeviceId == device.Id).ToListAsync(cancellationToken);
            _dbContext.DeviceProcesses.RemoveRange(existing);

            foreach (var proc in processes.Take(MaxProcesses))
            {
                if (string.IsNullOrWhiteSpace(proc.Name) || proc.ProcessId < 0)
                {
                    continue;
                }

                _dbContext.DeviceProcesses.Add(new DeviceProcessEntry(
                    device.Id, proc.ProcessId, Truncate(proc.Name, 256)!,
                    Math.Max(0, proc.WorkingSetBytes), Truncate(proc.ExecutablePath, 512), now));
            }
        }
    }

    private async Task ApplyWindowsUpdateAsync(
        Device device, InventoryWindowsUpdate update, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var history = (update.History ?? []).Take(MaxUpdateHistory).ToArray();
        var failedCount = history.Count(h => h.Result is "Failed" or "Aborted");

        var status = await _dbContext.DeviceUpdateStatus
            .SingleOrDefaultAsync(u => u.DeviceId == device.Id, cancellationToken);
        if (status is null)
        {
            status = new DeviceUpdateStatus(device.Id);
            _dbContext.DeviceUpdateStatus.Add(status);
        }

        status.Apply(update.RebootRequired, failedCount, now);

        var existing = await _dbContext.DeviceUpdateHistory
            .Where(h => h.DeviceId == device.Id).ToListAsync(cancellationToken);
        _dbContext.DeviceUpdateHistory.RemoveRange(existing);

        foreach (var h in history)
        {
            if (string.IsNullOrWhiteSpace(h.Title))
            {
                continue;
            }

            _dbContext.DeviceUpdateHistory.Add(new DeviceUpdateHistoryEntry(
                device.Id, Truncate(h.Title, 384)!, h.Date,
                Truncate(h.Operation, 32) ?? "Other", Truncate(h.Result, 32) ?? "Unknown", now));
        }
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
