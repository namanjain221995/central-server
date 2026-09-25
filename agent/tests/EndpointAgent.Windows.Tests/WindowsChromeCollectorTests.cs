using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using EndpointAgent.Core.Inventory.Chrome;
using EndpointAgent.Windows;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// Chrome discovery against this machine: whatever Chrome is or is not here, the
/// collector answers with a well-formed section and never with anyone's e-mail.
/// </summary>
/// <remarks>
/// <para>
/// Asserts the shape every machine satisfies -- every path local, every id
/// well-formed, every status and install type one the server knows -- and the
/// few facts any machine with Chrome satisfies. A machine without Chrome
/// exercises the NotInstalled route and the Chrome-specific tests return early.
/// What the parsers make of a file, and what the normalizer makes of the
/// parsers' output, is pinned separately with fixtures in the Core tests.
/// </para>
/// <para>
/// The current user's profile directory is found the way the service finds
/// everyone's: through the machine's profile list, by SID. The special-folder
/// API would answer for the test process, and the service has no such process
/// to ask for.
/// </para>
/// </remarks>
public sealed class WindowsChromeCollectorTests
{
    private const string UninstallEntry = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Google Chrome";

    private static WindowsChromeCollector Create() => new(NullLogger<WindowsChromeCollector>.Instance);

    private static Task<InventoryChrome> Collect() => Create().CollectAsync(CancellationToken.None).AsTask();

    private static string CurrentSid() => WindowsIdentity.GetCurrent().User!.Value;

    /// <summary>The fixed machine-wide location, from the machine's own Program Files variable.</summary>
    private static string? MachineChromePath()
    {
        var programFiles = Environment.GetEnvironmentVariable("ProgramW6432")
            ?? Environment.GetEnvironmentVariable("ProgramFiles");

        return programFiles is null
            ? null
            : Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe");
    }

    /// <summary>The version Windows records for the product, from either registry view, or null.</summary>
    private static string? RegisteredDisplayVersion()
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var entry = baseKey.OpenSubKey(UninstallEntry);
            if (entry?.GetValue("DisplayVersion") is string version && !string.IsNullOrWhiteSpace(version))
            {
                return version.Trim();
            }
        }

        return null;
    }

    [Fact]
    public async Task Collects_without_throwing_and_reports_a_status_the_server_understands()
    {
        var chrome = await Collect();

        chrome.ShouldNotBeNull();
        InventoryChrome.Statuses.ShouldContain(chrome.Status);
        chrome.Profiles.ShouldNotBeNull();
        chrome.Profiles.Count.ShouldBeLessThanOrEqualTo(InventoryChrome.MaxProfiles);
    }

    /// <summary>
    /// With Chrome in its fixed machine-wide location, the installation is found,
    /// is machine-scoped, and carries a version this machine genuinely has.
    /// </summary>
    [Fact]
    public async Task A_machine_wide_chrome_is_reported_with_the_version_its_executable_carries()
    {
        var path = MachineChromePath();
        if (path is null || !File.Exists(path))
        {
            // No machine-wide Chrome here: nothing this test can prove.
            return;
        }

        var chrome = await Collect();

        var installation = chrome.Installation.ShouldNotBeNull();
        installation.InstallationScope.ShouldBe("Machine");
        installation.InstalledForUser.ShouldBeNull();

        var executable = installation.ExecutablePath.ShouldNotBeNull();
        Path.IsPathFullyQualified(executable).ShouldBeTrue(executable);
        executable.ShouldEndWith(@"\chrome.exe", Case.Insensitive);
        File.Exists(executable).ShouldBeTrue(executable);

        // Windows' record of the product and the executable's own version
        // resource agree, except while an update is staged and waiting for Chrome
        // to restart: the registry then already names the new version while the
        // launcher still carries the old one. Either is a version this machine
        // genuinely has; anything else is a version read from nowhere.
        var fileVersion = FileVersionInfo.GetVersionInfo(path).ProductVersion;
        fileVersion.ShouldNotBeNullOrWhiteSpace();
        var accepted = new[] { fileVersion, RegisteredDisplayVersion() }
            .Where(v => v is not null)
            .Select(v => v!)
            .Distinct()
            .ToArray();
        installation.Version.ShouldBeOneOf(accepted);

        if (installation.Architecture is not null)
        {
            installation.Architecture.ShouldBeOneOf("x64", "x86", "arm64");
        }
    }

    /// <summary>
    /// Every profile is a directory under a real user's Chrome, on this machine,
    /// and never one of the two directories Chrome keeps that are not profiles.
    /// </summary>
    [Fact]
    public async Task Every_profile_is_a_local_directory_of_a_real_user_and_never_the_system_or_guest_profile()
    {
        var chrome = await Collect();

        foreach (var profile in chrome.Profiles)
        {
            Path.IsPathFullyQualified(profile.ProfilePath).ShouldBeTrue(profile.ProfilePath);
            profile.ProfilePath.ShouldNotStartWith(@"\\");
            profile.ProfilePath.ShouldEndWith("\\" + profile.ProfileKey, Case.Insensitive);
            profile.ProfileKey.ShouldNotBe("System Profile");
            profile.ProfileKey.ShouldNotBe("Guest Profile");
            WindowsUserHives.IsRealUserSid(profile.UserSid).ShouldBeTrue(profile.UserSid);
            profile.Extensions.Count.ShouldBeLessThanOrEqualTo(InventoryChromeProfile.MaxExtensions);
        }
    }

    [Fact]
    public async Task Every_extension_has_a_well_formed_id_and_an_install_type_the_server_understands()
    {
        var chrome = await Collect();

        foreach (var extension in chrome.Profiles.SelectMany(p => p.Extensions))
        {
            InventoryChromeExtension.IsValidExtensionId(extension.ExtensionId).ShouldBeTrue(extension.ExtensionId);
            InventoryChromeExtension.InstallTypes.ShouldContain(extension.InstallType);
            extension.IsManaged.ShouldBe(extension.InstallType is "ExternalPolicy" or "ExternalPolicyDownload");
        }

        // An extension recorded in both preference files is one row, not two.
        foreach (var profile in chrome.Profiles)
        {
            profile.Extensions.Select(e => e.ExtensionId).ShouldBeUnique();
        }
    }

    /// <summary>
    /// A user Chrome has run for has profiles in the report -- found by SID
    /// through the profile list, which is the only way the service has.
    /// </summary>
    [Fact]
    public async Task The_current_users_profiles_are_reported_when_chrome_has_run_for_them()
    {
        var sid = CurrentSid();
        var profileDirectory = WindowsUserHives.ProfilePath(sid);
        if (profileDirectory is null)
        {
            return;
        }

        var localState = Path.Combine(profileDirectory, "AppData", "Local", "Google", "Chrome", "User Data", "Local State");
        if (!File.Exists(localState))
        {
            // Chrome has never run for this user: nothing this test can prove.
            return;
        }

        var chrome = await Collect();

        // Whatever the status: Chrome's uninstaller leaves User Data behind, so
        // leftover profiles are carried under "NotInstalled" as well.
        chrome.Profiles.ShouldContain(p => p.UserSid == sid);
        chrome.Profiles.Where(p => p.UserSid == sid).ShouldAllBe(p => Directory.Exists(p.ProfilePath));
    }

    /// <summary>
    /// The one thing the report must never carry. Local State keeps the Google
    /// account beside every profile; the display name is the only field that
    /// reaches the wire, and it is not an address.
    /// </summary>
    [Fact]
    public async Task No_profile_name_carries_an_e_mail_address()
    {
        var chrome = await Collect();

        chrome.Profiles.ShouldAllBe(p => p.ProfileName == null || !p.ProfileName.Contains('@'));
    }

    /// <summary>
    /// Architecture comes from the updater's own record and nowhere else. In
    /// particular the older "x64-stable-multi-chrome" form is not read: it names
    /// a channel layout, not a proven bitness.
    /// </summary>
    [Theory]
    [InlineData("-arch_x64-statsdef_1", "x64")]
    [InlineData("-ARCH_X64-statsdef_0", "x64")]
    [InlineData("-arch_arm64-statsdef_1", "arm64")]
    [InlineData("-arch_x86-statsdef_1", "x86")]
    [InlineData("x64-stable-multi-chrome", null)]
    [InlineData("-statsdef_1", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Architecture_is_read_from_the_updaters_ap_value_and_nowhere_else(string? ap, string? expected)
    {
        WindowsChromeCollector.ArchitectureFromAp(ap).ShouldBe(expected);
    }

    /// <summary>
    /// Local State is a user-writable file, so a profile key is trusted only as a
    /// directory name: anything with a separator or a traversal is refused
    /// before it is combined with a path, and so is a spelling Win32 would
    /// quietly rewrite -- a trailing dot or space is stripped and a reserved
    /// device name names the device -- since two such keys could name one
    /// directory, or none.
    /// </summary>
    [Theory]
    [InlineData("Default", true)]
    [InlineData("Profile 12", true)]
    [InlineData("Person 1.old", true)]
    [InlineData("Console", true)]
    [InlineData("COM10", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData(@"..\..\Windows", false)]
    [InlineData(@"Profile 1\..\..", false)]
    [InlineData(@"C:\Users\Someone", false)]
    [InlineData("Profile/1", false)]
    [InlineData("Profile:1", false)]
    [InlineData("Profile?", false)]
    [InlineData("Default.", false)]
    [InlineData("Default ", false)]
    [InlineData("CON", false)]
    [InlineData("nul", false)]
    [InlineData("NUL.old", false)]
    [InlineData("COM1", false)]
    [InlineData("lpt9", false)]
    [InlineData(null, false)]
    public void Only_a_plain_directory_name_is_a_profile_key(string? key, bool plain)
    {
        WindowsChromeCollector.IsPlainProfileKey(key).ShouldBe(plain);
    }

    // ---- junctions ----------------------------------------------------------------------

    private const string GenuineSid = "S-1-5-21-1000000000-2000000000-3000000000-1001";
    private const string PlanterSid = "S-1-5-21-1000000000-2000000000-3000000000-1002";

    private static readonly string[] UserDataSegments = ["AppData", "Local", "Google", "Chrome", "User Data"];

    /// <summary>A Local State naming two profiles, with invented account fields beside them.</summary>
    private const string LocalStateFixture = """
        {
          "profile": {
            "info_cache": {
              "Default": {
                "active_time": 1700000000.25,
                "gaia_id": "100000000000000000001",
                "gaia_name": "Casey Example",
                "hosted_domain": "NO_HOSTED_DOMAIN",
                "is_managed": 0,
                "name": "Casey",
                "user_name": "casey.example@example.com"
              },
              "Profile 1": {
                "active_time": 1720000000,
                "is_managed": 0,
                "name": "Work"
              }
            },
            "last_used": "Default",
            "profiles_order": [ "Default", "Profile 1" ]
          }
        }
        """;

    /// <summary>A Secure Preferences with one Web Store extension, every value invented.</summary>
    private const string SecurePreferencesFixture = """
        {
           "extensions": {
              "settings": {
                 "cafebabecafebabecafebabecafebabe": {
                    "disable_reasons": [  ],
                    "first_install_time": "13434562575879741",
                    "from_webstore": true,
                    "location": 1,
                    "manifest": {
                       "manifest_version": 3,
                       "name": "Contoso Tab Tidy",
                       "update_url": "https://clients2.google.com/service/update2/crx",
                       "version": "2.0.14"
                    },
                    "path": "cafebabecafebabecafebabecafebabe\\2.0.14_0"
                 }
              }
           },
           "protection": {
              "super_mac": "FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210"
           }
        }
        """;

    /// <summary>
    /// The profile root is the machine's; everything below it is the user's, and
    /// a directory junction needs no privilege to plant. Every file API follows
    /// a junction wherever it sits in a path, so the check has to be made on
    /// every directory on the way to User Data, not only on the leaf.
    /// </summary>
    [Theory]
    [InlineData("AppData")]
    [InlineData("Local")]
    [InlineData("Google")]
    [InlineData("Chrome")]
    [InlineData("User Data")]
    public void A_junction_anywhere_between_the_profile_root_and_user_data_is_refused(string linkedSegment)
    {
        using var tree = new TempTree();
        var genuine = tree.MakeDirectory(["genuine", .. UserDataSegments]);

        // The planter's tree is real down to the linked segment, which is a
        // junction into the genuine tree at the same depth; below it every
        // directory is real, so a check on the leaf alone would pass.
        var depth = Array.IndexOf(UserDataSegments, linkedSegment);
        var above = tree.MakeDirectory(["planter", .. UserDataSegments.Take(depth)]);
        CreateJunction(Path.Combine(above, linkedSegment), tree.Under(["genuine", .. UserDataSegments.Take(depth + 1)]));

        var planter = tree.Under("planter");
        Directory.Exists(Path.Combine([planter, .. UserDataSegments]))
            .ShouldBeTrue("the junction must work for the test to prove anything");

        var collector = Create();
        collector.RealDirectoryChain(tree.Under("genuine"), UserDataSegments).ShouldBe(genuine);
        collector.RealDirectoryChain(planter, UserDataSegments).ShouldBeNull();
    }

    [Fact]
    public void A_chain_with_a_missing_segment_is_no_chain()
    {
        using var tree = new TempTree();
        tree.MakeDirectory("someone", "AppData", "Local");

        Create().RealDirectoryChain(tree.Under("someone"), UserDataSegments).ShouldBeNull();
    }

    /// <summary>
    /// The whole route, with files of the shape Chrome writes. Through a real
    /// chain the profile is read and the account name resolved; through a
    /// junction on User Data nothing is read and no name is resolved, although
    /// the leaf inside the target is a real directory and the file a real file
    /// -- exactly what a check on the leaf alone would have passed. A profile
    /// directory that is itself a junction is refused the same way.
    /// </summary>
    [Fact]
    public void Profiles_are_read_through_a_real_chain_and_never_through_a_junction()
    {
        using var tree = new TempTree();
        var genuineUserData = tree.MakeDirectory(["genuine", .. UserDataSegments]);
        File.WriteAllText(Path.Combine(genuineUserData, "Local State"), LocalStateFixture);
        var defaultProfile = tree.MakeDirectory(["genuine", .. UserDataSegments, "Default"]);
        File.WriteAllText(Path.Combine(defaultProfile, "Secure Preferences"), SecurePreferencesFixture);
        // Listed in Local State, but its directory is a junction onto Default.
        CreateJunction(Path.Combine(genuineUserData, "Profile 1"), defaultProfile);

        var planterChrome = tree.MakeDirectory("planter", "AppData", "Local", "Google", "Chrome");
        CreateJunction(Path.Combine(planterChrome, "User Data"), genuineUserData);
        File.Exists(Path.Combine(planterChrome, "User Data", "Default", "Secure Preferences"))
            .ShouldBeTrue("the junction must work for the test to prove anything");

        var collector = Create();

        var genuineAccount = new Lazy<string>(() => @"CONTOSO\casey");
        var genuine = new List<DiscoveredChromeProfile>();
        collector.ReadUserProfiles(new UserProfile(GenuineSid, tree.Under("genuine")), genuineAccount, genuine, CancellationToken.None);

        var profile = genuine.ShouldHaveSingleItem();
        profile.UserSid.ShouldBe(GenuineSid);
        profile.UserAccount.ShouldBe(@"CONTOSO\casey");
        profile.Info.ProfileKey.ShouldBe("Default");
        profile.Info.Name.ShouldBe("Casey");
        profile.ProfilePath.ShouldBe(defaultProfile);
        profile.SecurePreferences.ShouldHaveSingleItem().Name.ShouldBe("Contoso Tab Tidy");
        genuineAccount.IsValueCreated.ShouldBeTrue();

        var planterAccount = new Lazy<string>(() => @"CONTOSO\planter");
        var planted = new List<DiscoveredChromeProfile>();
        collector.ReadUserProfiles(new UserProfile(PlanterSid, tree.Under("planter")), planterAccount, planted, CancellationToken.None);

        planted.ShouldBeEmpty();
        planterAccount.IsValueCreated.ShouldBeFalse("no name is looked up for a user nothing is reported for");
    }

    /// <summary>
    /// Plants a directory junction at <paramref name="junction"/> pointing at
    /// <paramref name="target"/>: what <c>mklink /J</c> makes, with the same
    /// (absent) privilege requirement, through the agent's own CreateFile and
    /// DeviceIoControl imports rather than by starting anything.
    /// </summary>
    private static void CreateJunction(string junction, string target)
    {
        Directory.CreateDirectory(junction);

        // REPARSE_DATA_BUFFER for IO_REPARSE_TAG_MOUNT_POINT: an 8-byte header,
        // then four offsets/lengths, then the substitute (NT) name and the print
        // name, each NUL-terminated, in one path buffer.
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        var print = Encoding.Unicode.GetBytes(target);
        var pathBuffer = substitute.Length + 2 + print.Length + 2;
        var buffer = new byte[16 + pathBuffer];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0), 0xA0000003);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), (ushort)(8 + pathBuffer));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(6), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), (ushort)substitute.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), (ushort)(substitute.Length + 2));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), (ushort)print.Length);
        substitute.CopyTo(buffer, 16);
        print.CopyTo(buffer, 16 + substitute.Length + 2);

        const uint GenericWrite = 0x40000000;
        const uint ShareReadWrite = 0x00000003;
        const uint OpenExisting = 3;
        const uint BackupSemantics = 0x02000000;
        const uint OpenReparsePoint = 0x00200000;
        const uint FsctlSetReparsePoint = 0x000900A4;

        using var handle = UsbNative.CreateFile(
            junction, GenericWrite, ShareReadWrite, IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "opening the junction directory");
        }

        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            if (!UsbNative.DeviceIoControl(
                    handle, FsctlSetReparsePoint, pinned.AddrOfPinnedObject(), (uint)buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "setting the reparse point");
            }
        }
        finally
        {
            pinned.Free();
        }
    }

    /// <summary>A throwaway directory tree standing in for one or more user profiles.</summary>
    private sealed class TempTree : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"epa-chrome-{Guid.CreateVersion7():N}");

        public TempTree() => Directory.CreateDirectory(Root);

        public string Under(params string[] segments) => Path.Combine([Root, .. segments]);

        public string MakeDirectory(params string[] segments)
        {
            var path = Under(segments);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            try
            {
                // A recursive delete removes a junction rather than descending
                // into it, and every target is inside this tree anyway.
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover temp tree is not a test failure.
            }
        }
    }
}
