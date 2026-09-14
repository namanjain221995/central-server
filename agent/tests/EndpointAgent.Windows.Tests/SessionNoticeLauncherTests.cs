using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using EndpointAgent.Core.SessionNotice;
using EndpointAgent.Windows.SessionNotice;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// How the service gives a user who is already signed in a session notifier --
/// after an install, an update or a service restart -- and what that must never
/// become.
/// </summary>
/// <remarks>
/// <para>
/// The rules are tested against a stand-in for Windows, so every case -- several
/// sessions, one already served, one with nobody signed in, one that fails --
/// runs deterministically. The Windows calls themselves are then tested for real:
/// the genuine notifier executable is started in the test's own session through
/// the same <c>CreateProcessAsUser</c> path the service uses, with a token for the
/// test's own session standing in for the one the service gets from
/// <c>WTSQueryUserToken</c>, which needs a privilege no test run has.
/// </para>
/// <para>
/// Nothing here connects to an installed agent: the notifier started here looks
/// for the production pipe, finds none on a machine without the agent, and simply
/// waits until it is ended.
/// </para>
/// </remarks>
public sealed class SessionNoticeLauncherTests
{
    private static readonly RestartNotice Notice = new(DateTimeOffset.UtcNow.AddMinutes(10), 600);

    private static uint CurrentSession => (uint)Process.GetCurrentProcess().SessionId;

    private static string UniquePipe() => $"EndpointPlatformAgent.SessionNotice.Test.{Guid.NewGuid():N}";

    private static async Task<bool> EventuallyAsync(Func<bool> condition, int milliseconds = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return condition();
    }

    /// <summary>Windows, as far as the launcher is concerned, under the test's control.</summary>
    private sealed class FakeHost : ISessionProcessHost
    {
        private int _nextPid = 4000;

        public List<uint> Sessions { get; set; } = [];
        public HashSet<uint> NobodySignedIn { get; } = [];
        public HashSet<uint> Failing { get; } = [];
        public List<(uint Session, string Image, string WorkingDirectory)> Started { get; } = [];

        public IReadOnlyList<uint> InteractiveSessions() => Sessions;

        public int? StartInSession(uint sessionId, string imagePath, string workingDirectory)
        {
            if (Failing.Contains(sessionId))
            {
                throw new Win32Exception(5);
            }

            if (NobodySignedIn.Contains(sessionId))
            {
                return null;
            }

            Started.Add((sessionId, imagePath, workingDirectory));
            return ++_nextPid;
        }
    }

    /// <summary>A directory that looks like the install folder: the notifier is there.</summary>
    private static string DirectoryWithNotifier()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"notifier-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, SessionNoticeLauncher.ImageName), [0x4D, 0x5A]);
        return directory;
    }

    private static SessionNoticeLauncher Launcher(FakeHost host, string directory) =>
        new(host, directory, NullLogger.Instance);

    // ------------------------------------------------------------ the rules

    [Fact]
    public void Starts_the_notifier_in_every_interactive_session_that_has_none()
    {
        var directory = DirectoryWithNotifier();
        var host = new FakeHost { Sessions = [1, 2, 3] };

        var started = Launcher(host, directory).StartWhereMissing(new HashSet<uint> { 2 });

        started.ShouldBe([1u, 3u]);
        host.Started.Select(s => s.Session).ShouldBe([1u, 3u]);
        host.Started.ShouldAllBe(s => s.Image == Path.Combine(directory, "EndpointAgent.SessionNotice.exe"));
        host.Started.ShouldAllBe(s => s.WorkingDirectory == directory);
    }

    [Fact]
    public void Never_starts_anything_in_session_0_even_if_windows_lists_it()
    {
        var host = new FakeHost { Sessions = [0, 1] };

        Launcher(host, DirectoryWithNotifier()).StartWhereMissing(new HashSet<uint>());

        host.Started.Select(s => s.Session).ShouldBe([1u]);
    }

    [Fact]
    public void A_session_with_nobody_signed_in_is_left_alone()
    {
        var host = new FakeHost { Sessions = [1, 2] };
        host.NobodySignedIn.Add(1);

        var started = Launcher(host, DirectoryWithNotifier()).StartWhereMissing(new HashSet<uint>());

        started.ShouldBe([2u]);
    }

    [Fact]
    public void One_session_failing_does_not_stop_the_others_and_does_not_throw()
    {
        var host = new FakeHost { Sessions = [1, 2] };
        host.Failing.Add(1);

        var started = Should.NotThrow(() => Launcher(host, DirectoryWithNotifier()).StartWhereMissing(new HashSet<uint>()));

        started.ShouldBe([2u]);
    }

    [Fact]
    public void Starts_nothing_when_the_notifier_is_not_beside_the_service()
    {
        var empty = Path.Combine(Path.GetTempPath(), $"no-notifier-{Guid.NewGuid():N}");
        Directory.CreateDirectory(empty);
        var host = new FakeHost { Sessions = [1] };

        var started = Launcher(host, empty).StartWhereMissing(new HashSet<uint>());

        started.ShouldBeEmpty();
        host.Started.ShouldBeEmpty();
    }

    [Fact]
    public void The_same_session_listed_twice_gets_one_notifier()
    {
        var host = new FakeHost { Sessions = [1, 1] };

        Launcher(host, DirectoryWithNotifier()).StartWhereMissing(new HashSet<uint>());

        host.Started.Count.ShouldBe(1);
    }

    /// <summary>
    /// There is no way to make the launcher start anything else, or start the
    /// notifier with anything: its only public entry takes the sessions to skip.
    /// </summary>
    [Fact]
    public void The_launcher_has_no_parameter_through_which_a_program_or_an_argument_could_be_supplied()
    {
        SessionNoticeLauncher.ImageName.ShouldBe("EndpointAgent.SessionNotice.exe");

        var methods = typeof(SessionNoticeLauncher)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .ToList();

        methods.Select(m => m.Name).ShouldBe(["StartWhereMissing"]);
        methods.Single().GetParameters().Select(p => p.ParameterType).ShouldBe([typeof(IReadOnlySet<uint>)]);

        var start = typeof(WindowsSessionProcessHost).GetMethod("StartInSession")!;
        start.GetParameters().Select(p => p.Name).ShouldBe(["sessionId", "imagePath", "workingDirectory"],
            "the host takes the image and where to run it, and nothing that could become an argument");
    }

    // ------------------------------------------ the scenarios, on the server

    /// <summary>
    /// Fresh install, or update, with a user already signed in: the service starts
    /// and, once its pipe is listening, gives that user a notifier. Sign-in did
    /// not happen, and did not need to.
    /// </summary>
    [Fact]
    public async Task When_the_service_starts_every_user_already_signed_in_gets_a_notifier()
    {
        var host = new FakeHost { Sessions = [1, 2] };
        var server = new SessionNoticePipeServer(
            UniquePipe(), NullLogger<SessionNoticePipeServer>.Instance,
            launcher: Launcher(host, DirectoryWithNotifier()), clientExitWait: TimeSpan.FromMilliseconds(50));

        await server.StartAsync(CancellationToken.None);
        try
        {
            (await EventuallyAsync(() => host.Started.Count == 2)).ShouldBeTrue("both signed-in sessions must be given a notifier");
            host.Started.Select(s => s.Session).ShouldBe([1u, 2u]);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }

        // Once per service start, not once per connection or per retry.
        await Task.Delay(200);
        host.Started.Count.ShouldBe(2);
    }

    /// <summary>The service restarting while the user stays signed in: a notifier again.</summary>
    [Fact]
    public async Task A_service_restart_starts_the_notifier_again()
    {
        var host = new FakeHost { Sessions = [1] };
        var directory = DirectoryWithNotifier();

        for (var start = 0; start < 2; start++)
        {
            var server = new SessionNoticePipeServer(
                UniquePipe(), NullLogger<SessionNoticePipeServer>.Instance,
                launcher: Launcher(host, directory), clientExitWait: TimeSpan.FromMilliseconds(50));
            await server.StartAsync(CancellationToken.None);
            (await EventuallyAsync(() => host.Started.Count == start + 1)).ShouldBeTrue();
            await server.StopAsync(CancellationToken.None);
        }

        host.Started.Select(s => s.Session).ShouldBe([1u, 1u]);
    }

    /// <summary>
    /// A restart is announced. The session that already has a notifier connected
    /// is not given a second one; the session without gets one and receives the
    /// notice as the replay when it connects.
    /// </summary>
    [Fact]
    public async Task Announcing_a_restart_starts_a_notifier_only_where_none_is_connected()
    {
        var name = UniquePipe();
        var host = new FakeHost { Sessions = [CurrentSession, 77] };
        var server = new SessionNoticePipeServer(
            name, NullLogger<SessionNoticePipeServer>.Instance,
            launcher: Launcher(host, DirectoryWithNotifier()), clientExitWait: TimeSpan.FromMilliseconds(50));
        await server.StartAsync(CancellationToken.None);

        using var stop = new CancellationTokenSource();
        var reader = new SessionNoticeReader(name, _ => SessionNoticePipe.Trust.Trusted, TimeProvider.System);
        var reading = reader.RunAsync(stop.Token);

        try
        {
            (await EventuallyAsync(() => server.ConnectedSessions.Contains(CurrentSession))).ShouldBeTrue(
                "the reader in this session must be connected and known by session");
            host.Started.Clear();

            server.RestartScheduled(Notice);

            host.Started.Select(s => s.Session).ShouldBe([77u], "only the session without a notifier is started");
            (await EventuallyAsync(() => reader.Latest == Notice)).ShouldBeTrue("the connected session still gets the notice");
        }
        finally
        {
            stop.Cancel();
            await server.StopAsync(CancellationToken.None);
            await reading;
        }
    }

    /// <summary>
    /// One user signs out, another signs in. The old session is gone from Windows'
    /// list and is never started in again; the new one, if for any reason the
    /// Run key did not give it a notifier, gets one with the next restart.
    /// </summary>
    [Fact]
    public async Task After_a_sign_out_and_a_new_sign_in_the_new_session_is_the_one_served()
    {
        var host = new FakeHost { Sessions = [1] };
        var server = new SessionNoticePipeServer(
            UniquePipe(), NullLogger<SessionNoticePipeServer>.Instance,
            launcher: Launcher(host, DirectoryWithNotifier()), clientExitWait: TimeSpan.FromMilliseconds(50));
        await server.StartAsync(CancellationToken.None);

        try
        {
            (await EventuallyAsync(() => host.Started.Count == 1)).ShouldBeTrue();
            host.Sessions = [2];

            server.RestartScheduled(Notice);

            host.Started.Select(s => s.Session).ShouldBe([1u, 2u]);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>A server with no launcher -- every earlier test -- still announces exactly as before.</summary>
    [Fact]
    public async Task A_server_without_a_launcher_starts_nothing_and_still_serves()
    {
        var name = UniquePipe();
        var server = new SessionNoticePipeServer(name, NullLogger<SessionNoticePipeServer>.Instance);
        await server.StartAsync(CancellationToken.None);

        using var stop = new CancellationTokenSource();
        var reader = new SessionNoticeReader(name, _ => SessionNoticePipe.Trust.Trusted, TimeProvider.System);
        var reading = reader.RunAsync(stop.Token);

        try
        {
            (await EventuallyAsync(() => server.ConnectedCount == 1)).ShouldBeTrue();
            Should.NotThrow(() => server.RestartScheduled(Notice));
            (await EventuallyAsync(() => reader.Latest == Notice)).ShouldBeTrue();
        }
        finally
        {
            stop.Cancel();
            await server.StopAsync(CancellationToken.None);
            await reading;
        }
    }

    // ------------------------------------------------- the real Windows calls

    private static string RealNotifier()
    {
        var path = Path.Combine(AppContext.BaseDirectory, SessionNoticeLauncher.ImageName);
        File.Exists(path).ShouldBeTrue(
            $"the test project references the notifier so that {path} is built beside the tests");
        return path;
    }

    /// <summary>
    /// A token the test may start a process with: a restricted copy of its own,
    /// restricting nothing. Windows lets a process assign such a token to a child
    /// without <c>SeAssignPrimaryTokenPrivilege</c>, which the service holds and a
    /// test does not.
    /// </summary>
    private static SafeAccessTokenHandle TokenForThisSession(uint session)
    {
        session.ShouldBe(CurrentSession, "the stand-in can only produce a token for the test's own session");

        if (!NativeMethods.OpenProcessToken(Process.GetCurrentProcess().Handle, NativeMethods.TOKEN_ALL_ACCESS, out var own))
        {
            throw new Win32Exception();
        }

        using (own)
        {
            if (!NativeMethods.CreateRestrictedToken(own, 0, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, IntPtr.Zero, out var restricted))
            {
                throw new Win32Exception();
            }

            return restricted;
        }
    }

    private static WindowsIdentity IdentityOf(Process process)
    {
        if (!NativeMethods.OpenProcessToken(process.Handle, NativeMethods.TOKEN_QUERY, out var token))
        {
            throw new Win32Exception();
        }

        using (token)
        {
            return new WindowsIdentity(token.DangerousGetHandle());
        }
    }

    private static void EndQuietly(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
        }
    }

    [Fact]
    public void Windows_lists_this_session_as_interactive_and_never_session_0()
    {
        var sessions = new WindowsSessionProcessHost().InteractiveSessions();

        sessions.ShouldContain(CurrentSession);
        sessions.ShouldNotContain(0u);
    }

    /// <summary>
    /// The genuine notifier, started for real through the service's own code: it
    /// runs in this session, as this user and no more, keeps running, and a second
    /// copy started the same way finds the session mutex held and exits at once.
    /// </summary>
    [Fact]
    public async Task The_real_notifier_starts_in_this_session_as_this_user_and_a_second_copy_exits_at_once()
    {
        var image = RealNotifier();
        var host = new WindowsSessionProcessHost(TokenForThisSession);

        var first = host.StartInSession(CurrentSession, image, AppContext.BaseDirectory);
        first.ShouldNotBeNull();

        try
        {
            using var process = Process.GetProcessById(first.Value);
            await Task.Delay(1500);
            process.HasExited.ShouldBeFalse("the notifier must keep running, waiting for the service");
            process.SessionId.ShouldBe((int)CurrentSession);
            process.MainModule!.FileName.ShouldBe(image, StringCompareShould.IgnoreCase);

            using var identity = IdentityOf(process);
            identity.User.ShouldBe(WindowsIdentity.GetCurrent().User, "it runs as the session's user, not as the service");

            var second = host.StartInSession(CurrentSession, image, AppContext.BaseDirectory);
            second.ShouldNotBeNull();

            try
            {
                using var duplicate = Process.GetProcessById(second.Value);
                // Open a handle now, so the exit code can still be read after it is gone.
                _ = duplicate.Handle;
                duplicate.WaitForExit(10000).ShouldBeTrue("a second notifier in the same session must exit");
                duplicate.ExitCode.ShouldBe(0, "it exits quietly, not by crashing");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // Already gone before a handle could be opened: exited at once, as required.
            }

            process.HasExited.ShouldBeFalse("the first notifier is the one that stays");
        }
        finally
        {
            EndQuietly(first.Value);
        }
    }

    /// <summary>
    /// The notifier shares the service's runtime files. An installer replacing them
    /// asks, through Restart Manager, the processes holding them to close; the
    /// notifier must do so, or the upgrade would leave the old files in place until
    /// a reboot. Proven with a real Restart Manager session against the real process.
    /// </summary>
    [Fact]
    public async Task The_real_notifier_closes_when_restart_manager_asks_so_an_upgrade_can_replace_its_files()
    {
        var image = RealNotifier();
        var host = new WindowsSessionProcessHost(TokenForThisSession);
        var pid = host.StartInSession(CurrentSession, image, AppContext.BaseDirectory)!.Value;

        try
        {
            using var process = Process.GetProcessById(pid);
            await Task.Delay(1500);
            process.HasExited.ShouldBeFalse();

            var key = new StringBuilder(NativeMethods.CCH_RM_SESSION_KEY + 1);
            NativeMethods.RmStartSession(out var session, 0, key).ShouldBe(0);
            try
            {
                NativeMethods.RmRegisterResources(session, 1, [image], 0, IntPtr.Zero, 0, null).ShouldBe(0);

                var needed = 0u;
                var count = 0u;
                NativeMethods.RmGetList(session, out needed, ref count, null, out _).ShouldBe(NativeMethods.ERROR_MORE_DATA);
                var apps = new NativeMethods.RM_PROCESS_INFO[needed];
                count = needed;
                NativeMethods.RmGetList(session, out needed, ref count, apps, out _).ShouldBe(0);

                var notifier = apps.Take((int)count).SingleOrDefault(a => a.Process.dwProcessId == (uint)pid);
                notifier.Process.dwProcessId.ShouldBe((uint)pid, "Restart Manager must see the notifier holding the file");
                notifier.ApplicationType.ShouldNotBe(NativeMethods.RmUnknownApp,
                    "an application Restart Manager cannot classify can only be shut down by force");

                NativeMethods.RmShutdown(session, 0, IntPtr.Zero).ShouldBe(0, "a gentle shutdown request must succeed");
            }
            finally
            {
                NativeMethods.RmEndSession(session);
            }

            process.WaitForExit(10000).ShouldBeTrue("the notifier must have closed when asked");
        }
        finally
        {
            EndQuietly(pid);
        }
    }

    /// <summary>
    /// The token the notifier is started with is never the elevated half of a UAC
    /// pair. For an unelevated caller the only work is a duplicate; the elevated
    /// route -- the linked token -- needs the service's privilege to complete.
    /// </summary>
    [UnelevatedFact]
    public void The_token_used_is_primary_and_not_elevated()
    {
        if (!NativeMethods.OpenProcessToken(Process.GetCurrentProcess().Handle, NativeMethods.TOKEN_ALL_ACCESS, out var own))
        {
            throw new Win32Exception();
        }

        using (own)
        using (var token = SessionUserToken.WithoutElevation(own))
        {
            token.IsInvalid.ShouldBeFalse();
            SessionUserToken.IsElevated(token).ShouldBeFalse();
            using var identity = new WindowsIdentity(token.DangerousGetHandle());
            identity.ImpersonationLevel.ShouldBe(TokenImpersonationLevel.None, "a primary token, as CreateProcessAsUser requires");
            identity.User.ShouldBe(WindowsIdentity.GetCurrent().User);
        }
    }

    private static class NativeMethods
    {
        internal const uint TOKEN_QUERY = 0x0008;
        internal const uint TOKEN_ALL_ACCESS = 0xF01FF;
        internal const int CCH_RM_SESSION_KEY = 32;
        internal const int ERROR_MORE_DATA = 234;
        internal const int RmUnknownApp = 0;

        [StructLayout(LayoutKind.Sequential)]
        internal struct RM_UNIQUE_PROCESS
        {
            public uint dwProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
            public int ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateRestrictedToken(
            SafeAccessTokenHandle existingTokenHandle, uint flags, uint disableSidCount, IntPtr sidsToDisable,
            uint deletePrivilegeCount, IntPtr privilegesToDelete, uint restrictedSidCount, IntPtr sidsToRestrict,
            out SafeAccessTokenHandle newTokenHandle);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        internal static extern int RmStartSession(out uint sessionHandle, int sessionFlags, StringBuilder sessionKey);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        internal static extern int RmRegisterResources(
            uint sessionHandle, uint fileCount, string[] fileNames, uint applicationCount, IntPtr applications,
            uint serviceCount, string[]? serviceNames);

        [DllImport("rstrtmgr.dll")]
        internal static extern int RmGetList(
            uint sessionHandle, out uint procInfoNeeded, ref uint procInfo,
            [In, Out] RM_PROCESS_INFO[]? affectedApps, out uint rebootReasons);

        [DllImport("rstrtmgr.dll")]
        internal static extern int RmShutdown(uint sessionHandle, uint actionFlags, IntPtr statusCallback);

        [DllImport("rstrtmgr.dll")]
        internal static extern int RmEndSession(uint sessionHandle);
    }
}
