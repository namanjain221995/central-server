using System.IO.Compression;
using System.Threading.Channels;
using EndpointAgent.Core.Abstractions;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.FileCollection;

/// <summary>
/// Runs file collections off the heartbeat thread. The task executor only enqueues a
/// job here and returns at once, so zipping and uploading a large folder never blocks
/// the agent's heartbeat or other tasks.
/// </summary>
/// <remarks>
/// Each job: resolve the user's chosen folders, publish the encryption header, zip the
/// folders to a temporary file in-process (never by launching a shell — ADR-0005),
/// then read the archive back in chunks, encrypt each, and upload it with backpressure.
/// The temporary archive is always deleted. Nothing is persisted across a restart: a
/// collection interrupted by a restart simply fails, and the operator retries.
/// </remarks>
public sealed class CollectFilesWorker : BackgroundService
{
    /// <summary>Plaintext read per frame, leaving room for the nonce and tag within the frame cap.</summary>
    private static readonly int PlaintextChunkSize = FileCollectionProtocol.MaxFrameBytes - 64;

    private static readonly TimeSpan BackpressureRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>How many consecutive transient upload failures to tolerate before giving up a frame.</summary>
    private const int MaxTransientRetries = 15;

    private readonly Channel<CollectFilesJob> _queue =
        Channel.CreateBounded<CollectFilesJob>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
        });

    private readonly IUserFolderResolver _folders;
    private readonly IFileCollectionApiClient _apiClient;
    private readonly IDeviceCredentialStore _credentialStore;
    private readonly ILogger<CollectFilesWorker> _logger;

    public CollectFilesWorker(
        IUserFolderResolver folders,
        IFileCollectionApiClient apiClient,
        IDeviceCredentialStore credentialStore,
        ILogger<CollectFilesWorker> logger)
    {
        _folders = folders ?? throw new ArgumentNullException(nameof(folders));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Queues a collection. Returns false only if the queue is unexpectedly saturated.</summary>
    public bool Enqueue(CollectFilesJob job) => _queue.Writer.TryWrite(job);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await RunJobAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "File collection {TransferId} failed unexpectedly.", job.TransferId);
            }
        }
    }

    private async Task RunJobAsync(CollectFilesJob job, CancellationToken cancellationToken)
    {
        var credential = await _credentialStore.LoadAsync(cancellationToken);
        if (credential is null)
        {
            _logger.LogWarning("File collection {TransferId}: no device credential; cannot upload.", job.TransferId);
            return;
        }

        var resolved = _folders.Resolve(job.UserSid, job.RootKeys);
        if (resolved.Count == 0)
        {
            _logger.LogWarning(
                "File collection {TransferId}: none of {Roots} resolved for user {UserSid}.",
                job.TransferId, string.Join(",", job.RootKeys), job.UserSid);
            await _apiClient.PostFileCollectionResultAsync(
                job.TransferId,
                new AgentFileCollectionResult(false, "No matching folders were found for that user.", 0, 0, false),
                credential, cancellationToken);
            return;
        }

        var tempZip = Path.Combine(Path.GetTempPath(), $"epp-collect-{job.TransferId}.zip");
        try
        {
            using var session = FileCollectionCryptoSession.Create(job.RecipientPublicKeyBase64);

            var headerPosted = await _apiClient.PostFileCollectionHeaderAsync(
                job.TransferId,
                new AgentFileCollectionHeader(session.EphemeralPublicKeyBase64, session.SaltBase64),
                credential, cancellationToken);
            if (headerPosted.Status != AgentApiStatus.Success)
            {
                _logger.LogInformation(
                    "File collection {TransferId}: header post returned {Status}; abandoning.",
                    job.TransferId, headerPosted.Status);
                return;
            }

            var truncated = ZipFolders(resolved, tempZip, job.MaxBytes, job.TransferId);

            var uploaded = await UploadArchiveAsync(job, session, tempZip, credential, cancellationToken);
            if (uploaded is null)
            {
                // The browser went away mid-upload; the server already knows. Nothing
                // more to report.
                return;
            }

            await _apiClient.PostFileCollectionResultAsync(
                job.TransferId,
                new AgentFileCollectionResult(
                    Succeeded: true,
                    Message: truncated ? "The archive reached the size limit and is incomplete." : null,
                    TotalBytes: uploaded.Value.TotalBytes,
                    FrameCount: uploaded.Value.FrameCount,
                    Truncated: truncated),
                credential, cancellationToken);

            _logger.LogInformation(
                "File collection {TransferId} finished: {Frames} frames, {Bytes} ciphertext bytes, truncated={Truncated}.",
                job.TransferId, uploaded.Value.FrameCount, uploaded.Value.TotalBytes, truncated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "File collection {TransferId} failed.", job.TransferId);
            await TryReportFailureAsync(job.TransferId, credential, "The device could not complete the collection.");
        }
        finally
        {
            TryDelete(tempZip);
        }
    }

    /// <summary>
    /// Zips the resolved folders into <paramref name="zipPath"/>, each under its own
    /// top-level folder. Reparse points are skipped (no junction escape), unreadable
    /// files are skipped, and writing stops once the archive reaches
    /// <paramref name="maxBytes"/> — returning true when it was cut short.
    /// </summary>
    private bool ZipFolders(IReadOnlyList<ResolvedFolder> folders, string zipPath, long maxBytes, string transferId)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var folder in folders)
            {
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(folder.Path, "*", options);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "File collection {TransferId}: cannot enumerate {Folder}.", transferId, folder.Path);
                    continue;
                }

                foreach (var file in files)
                {
                    if (fileStream.Length >= maxBytes)
                    {
                        _logger.LogInformation(
                            "File collection {TransferId}: reached the {MaxBytes}-byte limit; truncating.",
                            transferId, maxBytes);
                        return true;
                    }

                    var relative = Path.GetRelativePath(folder.Path, file).Replace('\\', '/');
                    var entryName = $"{folder.Key}/{relative}";
                    try
                    {
                        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                        using var entryStream = entry.Open();
                        using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        source.CopyTo(entryStream);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogDebug(ex, "File collection {TransferId}: skipping unreadable {File}.", transferId, file);
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Reads the archive back, encrypts it frame by frame and uploads each with
    /// backpressure. Returns null if the transfer was abandoned (browser gone), else
    /// the ciphertext byte and frame totals.
    /// </summary>
    private async Task<(long TotalBytes, int FrameCount)?> UploadArchiveAsync(
        CollectFilesJob job,
        FileCollectionCryptoSession session,
        string zipPath,
        Abstractions.DeviceCredential credential,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[PlaintextChunkSize];
        long totalCiphertext = 0;
        long seq = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var read = await ReadFullAsync(source, buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            var frame = session.EncryptFrame(seq, buffer.AsSpan(0, read));

            var transient = 0;
            while (true)
            {
                var result = await _apiClient.UploadFileCollectionChunkAsync(job.TransferId, seq, frame, credential, cancellationToken);
                if (result.Status == AgentApiStatus.Success)
                {
                    break;
                }

                if (result.Status == AgentApiStatus.TransientFailure)
                {
                    // Backpressure or a flaky link: wait and retry the same frame.
                    if (++transient > MaxTransientRetries)
                    {
                        _logger.LogWarning(
                            "File collection {TransferId}: frame {Seq} failed after {Tries} transient retries.",
                            job.TransferId, seq, transient);
                        await TryReportFailureAsync(job.TransferId, credential, "The upload stalled and was abandoned.");
                        return null;
                    }

                    await Task.Delay(BackpressureRetryDelay, cancellationToken);
                    continue;
                }

                // Rejected / Unauthorized / NotFound: the transfer is gone. Stop quietly.
                _logger.LogInformation(
                    "File collection {TransferId}: frame {Seq} upload returned {Status}; stopping.",
                    job.TransferId, seq, result.Status);
                return null;
            }

            totalCiphertext += frame.Length;
            seq++;
        }

        return (totalCiphertext, (int)seq);
    }

    private async Task TryReportFailureAsync(string transferId, Abstractions.DeviceCredential credential, string message)
    {
        try
        {
            await _apiClient.PostFileCollectionResultAsync(
                transferId, new AgentFileCollectionResult(false, message, 0, 0, false), credential, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "File collection {TransferId}: could not report failure.", transferId);
        }
    }

    /// <summary>Fills the buffer as far as the stream allows, so frames are full except the last.</summary>
    private static async Task<int> ReadFullAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not delete temporary archive {Path}.", path);
        }
    }
}
