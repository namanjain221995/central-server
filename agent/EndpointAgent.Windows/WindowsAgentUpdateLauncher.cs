using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointAgent.Windows;

/// <summary>
/// Windows half of agent self-update: Authenticode verification and handing the
/// verified MSI to Windows through a one-shot Task Scheduler entry.
/// </summary>
/// <remarks>
/// <para>
/// Task Scheduler is what makes self-update survivable. The upgrade stops the
/// agent service, so nothing the agent runs in-process — and nothing whose
/// lifetime is tied to the agent — can carry the install to completion. A
/// scheduled task is executed by the Task Scheduler service as SYSTEM,
/// completely decoupled from this process: the agent registers it, reports its
/// task result, and dies at the installer's hand while msiexec keeps working.
/// </para>
/// <para>
/// This is not a command channel. The executable is the fixed system
/// <c>msiexec.exe</c>, the arguments are built here from exactly three values —
/// the path of a file this agent hash- and signature-verified moments ago, a log
/// path inside the agent's own log directory, and the server URL this agent is
/// already configured with — and the scheduled task is registered to run once and
/// delete itself. No dashboard-supplied string ever reaches the command line
/// (ADR-0005 upheld: the agent still launches no process; Windows does).
/// </para>
/// <para>
/// The server URL is passed as <c>SERVERBASEURL</c> so an update keeps the server
/// the endpoint is managed by, whatever URL the package was built with. A package
/// built against a development server once replaced a production agent's server
/// with <c>localhost</c>; with the URL carried forward, that package would at worst
/// update the binaries.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsAgentUpdateLauncher(
    ILogger<WindowsAgentUpdateLauncher> logger,
    IOptions<AgentOptions>? agentOptions = null) : IAgentUpdateLauncher
{
    private const string TaskName = "EndpointPlatformAgentUpdate";

    private readonly ILogger<WindowsAgentUpdateLauncher> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    private readonly string? _serverBaseUrl = agentOptions?.Value.ServerBaseUrl;

    public ValueTask<string?> VerifySignatureAsync(
        string msiPath, string? requiredSignerSubject, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (requiredSignerSubject is null)
        {
            // The release is published unsigned by explicit declaration. The
            // caller has already logged this loudly; there is no signature to
            // check and pretending to check one would be worse than honesty.
            return ValueTask.FromResult<string?>(null);
        }

        var trustResult = WinTrust.VerifyEmbeddedSignature(msiPath);
        if (trustResult != 0)
        {
            return ValueTask.FromResult<string?>(
                $"Authenticode verification failed (0x{trustResult:X8}).");
        }

        try
        {
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(msiPath));
            if (!cert.Subject.Contains(requiredSignerSubject, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Update MSI signer subject '{Subject}' does not contain the pinned '{Required}'.",
                    cert.Subject, requiredSignerSubject);
                return ValueTask.FromResult<string?>("Signer subject does not match the required publisher.");
            }
        }
        catch (Exception ex) when (ex is CryptographicException or IOException)
        {
            return ValueTask.FromResult<string?>("Could not read the update's signer certificate.");
        }

        return ValueTask.FromResult<string?>(null);
    }

    public ValueTask ScheduleInstallAsync(
        string msiPath, string installLogPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Fixed system binary; never resolved through PATH.
        var msiexec = Path.Combine(Environment.SystemDirectory, "msiexec.exe");

        var arguments = BuildArguments(msiPath, installLogPath, _serverBaseUrl);
        if (!arguments.Contains("SERVERBASEURL=", StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "The configured server URL could not be carried into the update; "
                + "the updated agent will use the URL its package was built with.");
        }

        var startAt = DateTime.Now.AddSeconds(15);

        // Task Scheduler COM (Schedule.Service). Late-bound: the interop
        // assemblies are not shipped, and the object model is stable since Vista.
        var scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)
            ?? throw new InvalidOperationException("Task Scheduler COM service is unavailable.");

        try
        {
            dynamic service = scheduler;
            service.Connect();

            dynamic definition = service.NewTask(0);
            definition.RegistrationInfo.Description =
                "One-shot Endpoint Platform agent update, registered by the agent after full verification.";
            definition.Settings.StartWhenAvailable = true;
            definition.Settings.DisallowStartIfOnBatteries = false;
            definition.Settings.StopIfGoingOnBatteries = false;
            // If everything goes right this entry is deleted right after running;
            // this bound cleans up even if deletion is interrupted.
            definition.Settings.DeleteExpiredTaskAfter = "PT0S";

            dynamic trigger = definition.Triggers.Create(1 /* TASK_TRIGGER_TIME */);
            trigger.StartBoundary = startAt.ToString("yyyy-MM-dd'T'HH:mm:ss");
            trigger.EndBoundary = startAt.AddHours(1).ToString("yyyy-MM-dd'T'HH:mm:ss");

            dynamic action = definition.Actions.Create(0 /* TASK_ACTION_EXEC */);
            action.Path = msiexec;
            action.Arguments = arguments;

            dynamic folder = service.GetFolder("\\");
            folder.RegisterTaskDefinition(
                TaskName,
                definition,
                6 /* TASK_CREATE_OR_UPDATE */,
                "SYSTEM",
                null,
                5 /* TASK_LOGON_SERVICE_ACCOUNT */,
                null);

            _logger.LogInformation(
                "Agent update install scheduled for {StartAt:HH:mm:ss} via Task Scheduler.", startAt);
        }
        finally
        {
            if (System.Runtime.InteropServices.Marshal.IsComObject(scheduler))
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(scheduler);
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>The msiexec command line for an update.</summary>
    /// <remarks>
    /// Quotes around both paths: they live under directories with spaces. Both are
    /// agent-authored — the MSI path was written by this process into its own
    /// protected state directory, the log path likewise. The server URL is carried
    /// only when it is an absolute http(s) URL with nothing in it that could end the
    /// quoted value; otherwise it is left out, never escaped into the command line.
    /// </remarks>
    internal static string BuildArguments(string msiPath, string installLogPath, string? serverBaseUrl)
    {
        var arguments = $"/i \"{msiPath}\" /qn REBOOT=ReallySuppress /l*v \"{installLogPath}\"";

        if (Uri.TryCreate(serverBaseUrl, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            && !serverBaseUrl.Any(c => c == '"' || char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            arguments += $" SERVERBASEURL=\"{serverBaseUrl}\"";
        }

        return arguments;
    }
}
