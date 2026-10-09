using System.Security.Cryptography;
using System.Text.Json;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Configuration;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointAgent.Core.Tasks;

/// <summary>
/// Updates the agent itself to an approved published release.
/// </summary>
/// <remarks>
/// <para>
/// The task payload is treated as a claim, not an instruction. The executor
/// fetches the server's published release metadata over its own authenticated
/// channel and refuses unless the payload and the server agree on release id,
/// version and SHA-256 — so even a forged or tampered task cannot choose what
/// gets installed; it can only name the one release the server already offers.
/// </para>
/// <para>
/// Every gate fails closed, in order: payload shape, server agreement, strictly
/// -newer version (no downgrades, no same-version reinstalls), architecture,
/// download, SHA-256 over the actual bytes, Authenticode signature when the
/// release declares a signer. Only a file that passed everything is handed to
/// the launcher — and the result is posted as "update started", never
/// "succeeded": success is the new agent heartbeating with the new version,
/// which no executor that is about to be stopped can truthfully report.
/// </para>
/// <para>
/// Before scheduling the install, the enrollment state files are snapshotted to
/// a backup directory inside the protected state folder. If the installer or an
/// older MSI's uninstall step damages the live state, the freshly-installed
/// service restores identity from the snapshot instead of re-enrolling as a
/// stranger.
/// </para>
/// </remarks>
public sealed class UpdateAgentExecutor(
    IAgentApiClient apiClient,
    IDeviceCredentialStore credentialStore,
    IAgentUpdateLauncher launcher,
    IOptions<AgentOptions> options,
    ILogger<UpdateAgentExecutor> logger) : ITaskExecutor
{
    /// <summary>
    /// Where the download is staged, resolved the way every other state-owning
    /// component resolves it.
    /// </summary>
    /// <remarks>
    /// In production the option is unset and this is <see cref="AgentPaths.StateDirectory"/>
    /// exactly as before -- byte-identical behaviour. It exists because the
    /// executor previously reached for that static path directly while its five
    /// siblings honoured the option, which made the download gate untestable
    /// anywhere <c>ProgramData\EndpointPlatformAgent</c> exists but is not
    /// writable by the test process: the write failed before the hash and
    /// signature gates these tests exist to prove, so they failed for a reason
    /// that had nothing to do with what they assert.
    /// </remarks>
    private readonly string _stateDirectory = options.Value.StateDirectory ?? AgentPaths.StateDirectory;

    public string TaskType => "UpdateAgent";

    public const string BackupDirectoryName = AgentStateRestore.BackupDirectoryName;

    public async Task<AgentTaskResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default)
    {
        // ---- Gate 1: payload shape -----------------------------------------
        Guid releaseId;
        string claimedVersion;
        string claimedSha;
        try
        {
            using var doc = JsonDocument.Parse(task.PayloadJson ?? "");
            releaseId = doc.RootElement.GetProperty("releaseId").GetGuid();
            claimedVersion = doc.RootElement.GetProperty("version").GetString() ?? "";
            claimedSha = (doc.RootElement.GetProperty("sha256").GetString() ?? "").ToLowerInvariant();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return new AgentTaskResult(false, "Malformed update-agent payload.", null);
        }

        var credential = await credentialStore.LoadAsync(cancellationToken);
        if (credential is null)
        {
            return new AgentTaskResult(false, "No device credential available.", null);
        }

        // ---- Gate 2: the server must agree with the payload ----------------
        var infoResult = await apiClient.GetAgentUpdateInfoAsync(credential, cancellationToken);
        if (!infoResult.IsSuccess || infoResult.Value is null)
        {
            return new AgentTaskResult(false, "Could not fetch release metadata from the server.", null);
        }

        var info = infoResult.Value;
        if (!info.Available || info.ReleaseId is null || info.Version is null || info.Sha256 is null)
        {
            return new AgentTaskResult(false, "The server offers no published agent release.", null);
        }

        if (info.ReleaseId != releaseId
            || !string.Equals(info.Version, claimedVersion, StringComparison.Ordinal)
            || !string.Equals(info.Sha256, claimedSha, StringComparison.Ordinal))
        {
            // The task and the server disagree — whichever is stale or hostile,
            // installing on the payload's say-so is exactly what must not happen.
            logger.LogWarning(
                "Update task names release {TaskRelease} v{TaskVersion} but the server offers {ServerRelease} v{ServerVersion}; refusing.",
                releaseId, claimedVersion, info.ReleaseId, info.Version);
            return new AgentTaskResult(false,
                "The update task does not match the release the server currently offers; not installed.", null);
        }

        // ---- Gate 3: strictly newer, right architecture --------------------
        if (!IsStrictlyNewer(info.Version, AgentVersion.Current))
        {
            return new AgentTaskResult(false,
                $"Release {info.Version} is not newer than the running agent {AgentVersion.Current}; "
                + "downgrades and same-version reinstalls are refused.", null);
        }

        if (!string.Equals(info.Architecture, "x64", StringComparison.OrdinalIgnoreCase))
        {
            return new AgentTaskResult(false,
                $"Release targets '{info.Architecture}' but this agent runs on x64; refusing.", null);
        }

        // ---- Gate 4: download + hash over the actual bytes -----------------
        var stateDir = _stateDirectory;

        // The installer creates this directory, so in production it is always
        // there -- which is exactly why its absence went unnoticed until a clean
        // CI machine ran these paths. An update must not fail because a
        // directory the agent owns is missing; creating it is free and correct.
        Directory.CreateDirectory(stateDir);

        var downloadPath = Path.Combine(stateDir, $"agent-update-{info.Version}.msi.partial");
        var msiPath = Path.Combine(stateDir, $"agent-update-{info.Version}.msi");

        try
        {
            await using (var destination = new FileStream(
                downloadPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var download = await apiClient.DownloadAgentUpdateAsync(
                    info.ReleaseId.Value, destination, credential, cancellationToken);
                if (!download.IsSuccess)
                {
                    return new AgentTaskResult(false, "Downloading the update failed; the current installation is untouched.", null);
                }
            }

            var actualSha = await ComputeSha256Async(downloadPath, cancellationToken);
            if (!string.Equals(actualSha, info.Sha256, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "Update MSI hash mismatch (expected {Expected}, got {Actual}); refusing install.",
                    info.Sha256, actualSha);
                return new AgentTaskResult(false, "Downloaded update failed its content-hash check; not installed.", null);
            }

            // Publish the verified bytes under the real name only after the hash held.
            File.Move(downloadPath, msiPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Update download could not be written to the state directory.");
            return new AgentTaskResult(false, "The update could not be stored locally; not installed.", null);
        }
        finally
        {
            TryDelete(downloadPath);
        }

        // ---- Gate 5: Authenticode ------------------------------------------
        var signatureError = await launcher.VerifySignatureAsync(msiPath, info.SignerSubject, cancellationToken);
        if (signatureError is not null)
        {
            TryDelete(msiPath);
            return new AgentTaskResult(false, $"Signature verification failed: {signatureError}", null);
        }

        if (info.SignerSubject is null)
        {
            // Deliberate, documented development stance — loud in the log, and
            // impossible to mistake for a verified signature.
            logger.LogWarning(
                "Release {Version} is published UNSIGNED; installing on hash verification alone.", info.Version);
        }

        // ---- Preserve identity, then hand over -----------------------------
        SnapshotStateFiles(stateDir);

        // Deliberately the machine-wide log directory, NOT the state directory
        // resolved above. Nothing on the update path creates this folder: the
        // installer's AgentLogFolder component and Serilog's file sink do, and
        // both are bound to the static path. msiexec /l*v against a directory
        // nobody created fails 1622 and installs nothing, so honouring an
        // override here would turn a working update into a failed one. It is
        // also where an administrator looks when an upgrade goes wrong.
        //
        // One log per task, named after the task id the dashboard shows: msiexec
        // /l*v overwrites, so a shared name left only the last of several
        // attempts readable -- and the last was the one that had nothing to say.
        var logPath = Path.Combine(AgentPaths.LogDirectory, $"agent-update-{info.Version}-{task.TaskId:N}.msi.log");
        await launcher.ScheduleInstallAsync(msiPath, logPath, cancellationToken);

        logger.LogWarning(
            "Agent update {From} -> {To} verified and scheduled; this service will be stopped by the installer.",
            AgentVersion.Current, info.Version);

        // "Started", not "succeeded": the proof of success is the new agent
        // heartbeating with the new version, which this process cannot observe.
        return new AgentTaskResult(true,
            $"Update to {info.Version} verified and started; the agent will restart and report the new version.", null);
    }

    /// <summary>Strict numeric three-part comparison; anything unparseable is never newer.</summary>
    internal static bool IsStrictlyNewer(string candidate, string installed)
    {
        return TryParse(candidate, out var c) && TryParse(installed, out var i) && c > i;

        static bool TryParse(string value, out Version version)
        {
            version = new Version(0, 0, 0);
            var parts = value.Trim().Split('.');
            if (parts.Length != 3 || parts.Any(p => p.Length is 0 or > 9 || !p.All(char.IsAsciiDigit)))
            {
                return false;
            }

            version = new Version(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
            return true;
        }
    }

    /// <summary>
    /// Copies the identity-bearing state files into a backup folder the new
    /// service checks on start. DPAPI-protected content survives copying: the
    /// protection is machine-scoped, and the machine is not changing.
    /// </summary>
    private void SnapshotStateFiles(string stateDir)
    {
        try
        {
            var backupDir = Path.Combine(stateDir, BackupDirectoryName);
            Directory.CreateDirectory(backupDir);

            foreach (var file in Directory.EnumerateFiles(stateDir, "*.bin"))
            {
                File.Copy(file, Path.Combine(backupDir, Path.GetFileName(file)), overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The snapshot is a safety net, not a gate: a failed backup is logged
            // and the update proceeds, because refusing updates over it would
            // trade a recoverable risk for a permanent one.
            logger.LogWarning(ex, "Could not snapshot state files before the update.");
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best effort; a leftover partial file is inert.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
