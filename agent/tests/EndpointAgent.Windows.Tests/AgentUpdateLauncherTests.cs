using EndpointAgent.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// The Windows update launcher against the real OS: WinVerifyTrust on real
/// files, and a real Task Scheduler registration (elevated only).
/// </summary>
public sealed class AgentUpdateLauncherTests
{
    private static WindowsAgentUpdateLauncher Launcher() =>
        new(NullLogger<WindowsAgentUpdateLauncher>.Instance);

    [Fact]
    public async Task An_unsigned_file_with_a_required_signer_is_refused()
    {
        // Any unsigned bytes stand in for an unsigned MSI: WinVerifyTrust rejects
        // the missing signature before the format ever matters.
        var path = Path.Combine(Path.GetTempPath(), $"unsigned-{Guid.NewGuid():N}.msi");
        await File.WriteAllBytesAsync(path, new byte[2048]);

        try
        {
            var error = await Launcher().VerifySignatureAsync(path, "CN=Endpoint Platform");

            error.ShouldNotBeNull();
            error!.ShouldContain("Authenticode");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_signed_file_with_the_wrong_pinned_subject_is_refused_and_the_right_one_accepted()
    {
        // dotnet.exe rather than a System32 binary: most system files are
        // catalog-signed, which WinVerifyTrust's embedded check rejects before
        // the pin is ever consulted. The .NET host carries a real embedded
        // Authenticode signature, and it must exist on any machine running
        // these tests.
        var signedFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        File.Exists(signedFile).ShouldBeTrue("the .NET host must exist where these tests run");

        // Trust passes but publisher identity does not: refused at the pin.
        var wrongPin = await Launcher().VerifySignatureAsync(signedFile, "CN=Definitely Not The Publisher");
        wrongPin.ShouldNotBeNull();
        wrongPin!.ShouldContain("Signer subject");

        // And the true subject, read from the file itself, is accepted.
        using var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(
            System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(signedFile));
        var realSubjectFragment = cert.Subject.Split(',')[0]; // e.g. "CN=.NET"
        (await Launcher().VerifySignatureAsync(signedFile, realSubjectFragment)).ShouldBeNull();
    }

    [Fact]
    public async Task A_null_signer_skips_the_signature_gate_by_declaration()
    {
        var path = Path.Combine(Path.GetTempPath(), $"unsigned-{Guid.NewGuid():N}.msi");
        await File.WriteAllBytesAsync(path, new byte[128]);

        try
        {
            // The unsigned-release policy: no signer declared, no signature checked.
            (await Launcher().VerifySignatureAsync(path, null)).ShouldBeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_update_carries_the_running_agents_server_url_into_the_installer()
    {
        var arguments = WindowsAgentUpdateLauncher.BuildArguments(
            @"C:\ProgramData\EndpointPlatformAgent\agent-update-1.16.1.msi",
            @"C:\ProgramData\EndpointPlatformAgent\Logs\agent-update-1.16.1.msi.log",
            "https://endpoint.example.com");

        arguments.ShouldBe(
            "/i \"C:\\ProgramData\\EndpointPlatformAgent\\agent-update-1.16.1.msi\" /qn REBOOT=ReallySuppress "
            + "/l*v \"C:\\ProgramData\\EndpointPlatformAgent\\Logs\\agent-update-1.16.1.msi.log\" "
            + "SERVERBASEURL=\"https://endpoint.example.com\"");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("file:///C:/Windows")]
    [InlineData("https://endpoint.example.com\" /x \"C:\\other.msi")]
    [InlineData("https://endpoint.example.com/ REBOOT=Force")]
    public void A_server_url_that_is_not_a_plain_http_url_is_left_off_the_command_line(string? serverBaseUrl)
    {
        var arguments = WindowsAgentUpdateLauncher.BuildArguments(@"C:\a.msi", @"C:\a.log", serverBaseUrl);

        arguments.ShouldBe("/i \"C:\\a.msi\" /qn REBOOT=ReallySuppress /l*v \"C:\\a.log\"");
    }

    [ElevatedFact]
    public async Task Scheduling_registers_a_real_one_shot_task_scheduler_entry()
    {
        // Registers the genuine scheduled task. The MSI path deliberately does
        // not exist: when the trigger fires, msiexec exits 1619 quietly under
        // /qn — nothing is installed and no UI appears. The assertion is about
        // the registration itself, made through the same COM surface.
        var bogusMsi = Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.msi");
        var logPath = Path.Combine(Path.GetTempPath(), $"update-test-{Guid.NewGuid():N}.log");

        await Launcher().ScheduleInstallAsync(bogusMsi, logPath);

        var scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        try
        {
            dynamic service = scheduler;
            service.Connect();
            dynamic folder = service.GetFolder("\\");

            dynamic task = folder.GetTask("EndpointPlatformAgentUpdate");
            string arguments = task.Definition.Actions[1].Arguments;
            string path = task.Definition.Actions[1].Path;

            path.ShouldEndWith("msiexec.exe");
            arguments.ShouldContain(bogusMsi);
            arguments.ShouldContain("/qn");
            arguments.ShouldContain("REBOOT=ReallySuppress");

            // Clean up before the 15s trigger can fire.
            folder.DeleteTask("EndpointPlatformAgentUpdate", 0);
        }
        finally
        {
            if (System.Runtime.InteropServices.Marshal.IsComObject(scheduler))
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(scheduler);
            }
        }
    }
}
