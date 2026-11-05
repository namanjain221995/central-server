using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using EndpointAgent.Core.Abstractions;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Core.FileCollection;

/// <summary>
/// Runs file collections and browse listings off the heartbeat thread. The task
/// executors only enqueue a job here and return at once, so zipping, enumerating and
/// uploading never block the agent's heartbeat or other tasks.
/// </summary>
/// <remarks>
/// Both a collection (zip the chosen, or selected, files) and a browse (enumerate names
/// and sizes) end the same way: encrypt the bytes to the browser's one-time key and
/// upload them through the relay with backpressure. Nothing is persisted across a
/// restart — an interrupted job simply fails and the operator retries.
/// </remarks>
public sealed class CollectFilesWorker : BackgroundService
{
    /// <summary>Plaintext read per frame, leaving room for the nonce and tag within the frame cap.</summary>
    private static readonly int PlaintextChunkSize = FileCollectionProtocol.MaxFrameBytes - 64;

    private static readonly TimeSpan BackpressureRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>How many consecutive transient upload failures to tolerate before giving up a frame.</summary>
    private const int MaxTransientRetries = 15;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Channel<IFileCollectionJob> _queue =
        Channel.CreateBounded<IFileCollectionJob>(new BoundedChannelOptions(32)
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

    /// <summary>Queues a collection or a browse. Returns false only if the queue is unexpectedly saturated.</summary>
    public bool Enqueue(IFileCollectionJob job) => _queue.Writer.TryWrite(job);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var credential = await _credentialStore.LoadAsync(stoppingToken);
                if (credential is null)
                {
                    _logger.LogWarning("File job {TransferId}: no device credential; cannot upload.", job.TransferId);
                    continue;
                }

                switch (job)
                {
                    case CollectFilesJob collect:
                        await RunCollectAsync(collect, credential, stoppingToken);
                        break;
                    case ListFilesJob list:
                        await RunListAsync(list, credential, stoppingToken);
                        break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "File job {TransferId} failed unexpectedly.", job.TransferId);
            }
        }
    }

    // ------------------------------------------------------------------- collect

    private async Task RunCollectAsync(CollectFilesJob job, Abstractions.DeviceCredential credential, CancellationToken cancellationToken)
    {
        var resolved = _folders.Resolve(job.UserSid, job.RootKeys);
        if (resolved.Count == 0)
        {
            await ReportAsync(job.TransferId, new AgentFileCollectionResult(
                false, "No matching folders were found for that user.", 0, 0, false), credential, cancellationToken);
            return;
        }

        var tempZip = Path.Combine(Path.GetTempPath(), $"epp-collect-{job.TransferId}.zip");
        try
        {
            var truncated = ZipFolders(resolved, tempZip, job.MaxBytes, job.SelectedPaths, job.TransferId);

            await using var source = new FileStream(tempZip, FileMode.Open, FileAccess.Read, FileShare.Read);
            var uploaded = await UploadStreamAsync(job, source, credential, cancellationToken);
            if (uploaded is null)
            {
                return; // browser gone; the server already knows
            }

            await ReportAsync(job.TransferId, new AgentFileCollectionResult(
                Succeeded: true,
                Message: truncated ? "The archive reached the size limit and is incomplete." : null,
                TotalBytes: uploaded.Value.TotalBytes,
                FrameCount: uploaded.Value.FrameCount,
                Truncated: truncated), credential, cancellationToken);

            _logger.LogInformation(
                "File collection {TransferId} finished: {Frames} frames, {Bytes} bytes, truncated={Truncated}.",
                job.TransferId, uploaded.Value.FrameCount, uploaded.Value.TotalBytes, truncated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "File collection {TransferId} failed.", job.TransferId);
            await ReportFailureAsync(job.TransferId, credential, "The device could not complete the collection.");
        }
        finally
        {
            TryDelete(tempZip);
        }
    }

    /// <summary>
    /// Zips the resolved folders into <paramref name="zipPath"/>, each under its own
    /// top-level folder. When <paramref name="selected"/> is non-empty only the selected
    /// entries are included. Reparse points are skipped (no junction escape), unreadable
    /// files are skipped, and writing stops once the archive reaches
    /// <paramref name="maxBytes"/> — returning true when it was cut short.
    /// </summary>
    private bool ZipFolders(
        IReadOnlyList<ResolvedFolder> folders, string zipPath, long maxBytes,
        IReadOnlyList<string>? selected, string transferId)
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

                    if (!FileTreeEnumerator.IsSelected(entryName, selected))
                    {
                        continue;
                    }

                    try
                    {
                        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                        using var entryStream = entry.Open();
                        using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        src.CopyTo(entryStream);
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

    // ---------------------------------------------------------------------- list

    private async Task RunListAsync(ListFilesJob job, Abstractions.DeviceCredential credential, CancellationToken cancellationToken)
    {
        try
        {
            var resolved = _folders.Resolve(job.UserSid, job.RootKeys);
            var listing = FileTreeEnumerator.Enumerate(resolved, job.MaxEntries);
            var json = JsonSerializer.SerializeToUtf8Bytes(listing, JsonOptions);

            using var source = new MemoryStream(json, writable: false);
            var uploaded = await UploadStreamAsync(job, source, credential, cancellationToken);
            if (uploaded is null)
            {
                return;
            }

            await ReportAsync(job.TransferId, new AgentFileCollectionResult(
                Succeeded: true, Message: null, TotalBytes: uploaded.Value.TotalBytes,
                FrameCount: uploaded.Value.FrameCount, Truncated: listing.Truncated), credential, cancellationToken);

            _logger.LogInformation(
                "File listing {TransferId} finished: {Count} entries, truncated={Truncated}.",
                job.TransferId, listing.Entries.Count, listing.Truncated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "File listing {TransferId} failed.", job.TransferId);
            await ReportFailureAsync(job.TransferId, credential, "The device could not list the folders.");
        }
    }

    // -------------------------------------------------------------- shared upload

    /// <summary>
    /// Posts the encryption header, then reads <paramref name="source"/>, encrypts it
    /// frame by frame and uploads each with backpressure. Returns null if the transfer
    /// was abandoned (browser gone), else the ciphertext byte and frame totals.
    /// </summary>
    private async Task<(long TotalBytes, int FrameCount)?> UploadStreamAsync(
        IFileCollectionJob job, Stream source, Abstractions.DeviceCredential credential, CancellationToken cancellationToken)
    {
        using var session = FileCollectionCryptoSession.Create(job.RecipientPublicKeyBase64);

        var headerPosted = await _apiClient.PostFileCollectionHeaderAsync(
            job.TransferId,
            new AgentFileCollectionHeader(session.EphemeralPublicKeyBase64, session.SaltBase64),
            credential, cancellationToken);
        if (headerPosted.Status != AgentApiStatus.Success)
        {
            _logger.LogInformation("File job {TransferId}: header post returned {Status}; abandoning.",
                job.TransferId, headerPosted.Status);
            return null;
        }

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
                    if (++transient > MaxTransientRetries)
                    {
                        _logger.LogWarning("File job {TransferId}: frame {Seq} failed after {Tries} transient retries.",
                            job.TransferId, seq, transient);
                        await ReportFailureAsync(job.TransferId, credential, "The upload stalled and was abandoned.");
                        return null;
                    }

                    await Task.Delay(BackpressureRetryDelay, cancellationToken);
                    continue;
                }

                _logger.LogInformation("File job {TransferId}: frame {Seq} upload returned {Status}; stopping.",
                    job.TransferId, seq, result.Status);
                return null;
            }

            totalCiphertext += frame.Length;
            seq++;
        }

        return (totalCiphertext, (int)seq);
    }

    private async Task ReportAsync(string transferId, AgentFileCollectionResult result, Abstractions.DeviceCredential credential, CancellationToken cancellationToken)
    {
        await _apiClient.PostFileCollectionResultAsync(transferId, result, credential, cancellationToken);
    }

    private async Task ReportFailureAsync(string transferId, Abstractions.DeviceCredential credential, string message)
    {
        try
        {
            await _apiClient.PostFileCollectionResultAsync(
                transferId, new AgentFileCollectionResult(false, message, 0, 0, false), credential, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "File job {TransferId}: could not report failure.", transferId);
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
