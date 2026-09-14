using System.Reflection;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// Enforces the agent's "no shell execution" rule (ADR-0005).
/// </summary>
/// <remarks>
/// <para>
/// The agent runs as LocalSystem. The single most dangerous pattern it could adopt
/// is composing a command string and handing it to a shell (cmd/PowerShell),
/// because every string on that path becomes a potential privileged injection.
/// </para>
/// <para>
/// The precise, enforceable guarantees:
/// </para>
/// <list type="number">
///   <item><b>Core stays OS-agnostic:</b> it references no process API at all.</item>
///   <item><b>Nothing creates a process, except one reviewed file:</b> a source
///   scan over every agent .cs file asserts that every process-creating API --
///   <c>Process.Start</c>, <c>CreateProcess*</c>, <c>ShellExecute*</c>,
///   <c>WinExec</c> -- is absent everywhere but <c>WindowsSessionProcessHost.cs</c>,
///   and a second test pins what that file does: start one fixed image with no
///   arguments, no inherited handles, as the session's user. Reviewed process
///   <em>control</em> - <c>ServiceController</c>, <c>Process.GetProcessById</c>,
///   <c>Process.Kill</c> with an expected-image guard (Phase 9) - is permitted,
///   because it takes typed arguments and has no command line to inject into.</item>
///   <item><b>No PowerShell SDK</b> in either assembly.</item>
/// </list>
/// <para>
/// If a future feature genuinely needs to run something else (approved-script
/// execution, Phase 10-full), it arrives behind the signed-script pipeline and
/// this scan is tightened to allow exactly that reviewed call site too.
/// </para>
/// </remarks>
public sealed class AgentSafetyTests
{
    private static readonly Assembly AgentCore = typeof(EndpointAgent.Core.Configuration.AgentOptions).Assembly;
    private static readonly Assembly AgentWindows = typeof(EndpointAgent.Windows.WindowsSystemInfoProvider).Assembly;

    /// <summary>Every way a Windows program can be made to start another.</summary>
    private static readonly string[] ProcessCreationApis =
    [
        "Process.Start",
        "CreateProcess",
        "ShellExecute",
        "WinExec",
        "CreateProcessWithLogon",
        "CreateProcessWithToken",
    ];

    /// <summary>The one file allowed to create a process, and only the way the test below pins.</summary>
    private const string SessionProcessHostFile = "WindowsSessionProcessHost.cs";

    [Fact]
    public void Core_references_no_process_api_at_all()
    {
        // Core is platform-neutral logic; it must never touch OS process APIs.
        AgentCore.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty)
            .ShouldNotContain("System.Diagnostics.Process",
                "EndpointAgent.Core must stay OS-agnostic (ADR-0005)");
    }

    [Fact]
    public void No_agent_source_file_creates_a_process_except_the_session_process_host()
    {
        var agentRoot = FindAgentSourceRoot();

        var offenders = AgentSourceFiles(agentRoot)
            .Where(f => Path.GetFileName(f) != SessionProcessHostFile)
            .Where(f => ProcessCreationApis.Any(api => File.ReadAllText(f).Contains(api, StringComparison.Ordinal)))
            .Select(f => Path.GetFileName(f))
            .ToArray();

        offenders.ShouldBeEmpty(
            "process creation is the launch vector ADR-0005 forbids; found in: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// The one allowed call site, pinned. It starts the notifier in a user's
    /// session and nothing else: the image is passed as the application name (so
    /// Windows never parses a path out of a command line), the command line is that
    /// image quoted and nothing more, no handle crosses into the user's process,
    /// the process lands on the interactive desktop, and there is no parameter
    /// anywhere through which an argument could be supplied.
    /// </summary>
    [Fact]
    public void The_session_process_host_starts_one_fixed_image_with_no_arguments_and_no_inherited_handles()
    {
        var agentRoot = FindAgentSourceRoot();
        var file = AgentSourceFiles(agentRoot).Single(f => Path.GetFileName(f) == SessionProcessHostFile);
        var source = File.ReadAllText(file);

        // One P/Invoke declaration and one call; nothing else in the agent may name it.
        CountOf(source, "CreateProcessAsUserW(").ShouldBe(2, "exactly one declaration and one call");
        foreach (var api in ProcessCreationApis.Where(a => a != "CreateProcess"))
        {
            source.ShouldNotContain(api);
        }

        source.ShouldContain("lpApplicationName: imagePath");
        source.ShouldContain(".Append('\"').Append(imagePath).Append('\"')", Case.Sensitive, "the command line is the quoted image and nothing else");
        source.ShouldContain("bInheritHandles: false");
        source.ShouldContain(@"winsta0\default");
        source.ShouldNotContain("Environment.GetEnvironmentVariable");
        source.ShouldNotContain("Registry.");

        var start = typeof(EndpointAgent.Windows.SessionNotice.WindowsSessionProcessHost).GetMethod("StartInSession")!;
        start.GetParameters().Select(p => p.Name).ShouldBe(["sessionId", "imagePath", "workingDirectory"]);

        // And the only caller resolves that image from a constant name beside the service.
        EndpointAgent.Windows.SessionNotice.SessionNoticeLauncher.ImageName.ShouldBe("EndpointAgent.SessionNotice.exe");
    }

    /// <summary>
    /// The session notifier runs in every signed-in user's session, which makes it
    /// the component where a launch capability would do the most harm. The scan
    /// above covers it only because it lives under <c>agent/</c>; this fails if it
    /// is ever moved somewhere the scan does not look, and pins that it creates no
    /// process of its own.
    /// </summary>
    [Fact]
    public void The_session_notifier_is_inside_the_process_creation_scan_and_creates_none()
    {
        var agentRoot = FindAgentSourceRoot();
        var notifierSources = Directory
            .EnumerateFiles(Path.Combine(agentRoot, "EndpointAgent.SessionNotice"), "*.cs", SearchOption.TopDirectoryOnly)
            .ToList();

        notifierSources.ShouldNotBeEmpty("the session notifier's sources must sit under the scanned agent tree");
        notifierSources.ShouldAllBe(f => !ProcessCreationApis.Any(api => File.ReadAllText(f).Contains(api, StringComparison.Ordinal)));
    }

    private static IEnumerable<string> AgentSourceFiles(string agentRoot) =>
        Directory
            .EnumerateFiles(agentRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// The notifier references only what it needs, and in particular nothing that
    /// could run a script or start a process on its behalf.
    /// </summary>
    [Fact]
    public void The_session_notifier_does_not_reference_the_powershell_sdk_or_a_ui_framework()
    {
        var agentRoot = FindAgentSourceRoot();
        var project = File.ReadAllText(Path.Combine(agentRoot, "EndpointAgent.SessionNotice", "EndpointAgent.SessionNotice.csproj"));

        project.ShouldNotContain("PowerShell", Case.Insensitive);
        project.ShouldNotContain("System.Management.Automation", Case.Insensitive);
        project.ShouldNotContain("UseWindowsForms", Case.Insensitive, "a UI framework would pull in the desktop runtime");
        project.ShouldNotContain("UseWPF", Case.Insensitive);
    }

    [Theory]
    [MemberData(nameof(AgentAssemblies))]
    public void Agent_assemblies_do_not_reference_the_powershell_sdk(string assemblyName)
    {
        var assembly = ResolveAssembly(assemblyName);

        var references = assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToArray();

        references.ShouldNotContain(r => r.StartsWith("System.Management.Automation", StringComparison.Ordinal),
            $"{assemblyName} must not embed PowerShell (ADR-0005)");
        references.ShouldNotContain(r => r.StartsWith("Microsoft.PowerShell", StringComparison.Ordinal),
            $"{assemblyName} must not embed PowerShell (ADR-0005)");
    }

    public static TheoryData<string> AgentAssemblies() =>
        new(AgentCore.GetName().Name!, AgentWindows.GetName().Name!);

    private static Assembly ResolveAssembly(string name) =>
        name == AgentCore.GetName().Name ? AgentCore : AgentWindows;

    /// <summary>Walks up from the test binary to the repo, then to the <c>agent</c> tree.</summary>
    private static string FindAgentSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EndpointPlatform.slnx")))
            {
                var agent = Path.Combine(dir.FullName, "agent");
                Directory.Exists(agent).ShouldBeTrue("expected an 'agent' directory at the repo root");
                return agent;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root (EndpointPlatform.slnx).");
    }
}
