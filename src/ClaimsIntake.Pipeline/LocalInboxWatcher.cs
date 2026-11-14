using System.Collections.Concurrent;
using ClaimsIntake.Pipeline.Handoff;
using ClaimsIntake.Pipeline.Legacy;
using ClaimsIntake.Pipeline.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClaimsIntake.Pipeline;

public sealed class LocalInboxOptions
{
    public const string SectionName = "LocalInbox";

    /// <summary>The folder to watch. Documents dropped here are processed.</summary>
    public string Path { get; set; } = "./local-inbox";

    /// <summary>How often the folder is scanned.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How many times a document is tried when the legacy system or the review API is down, before it is moved to failed.</summary>
    public int MaxAttempts { get; set; } = 5;
}

/// <summary>
/// Local mode stand-in for the blob trigger (ADR 0005). It scans a folder, runs each new document through
/// <see cref="ClaimIntakePipeline"/> and moves it to <c>processed/</c>, or to <c>failed/</c> when it cannot
/// be read. When a dependency is down the file stays put and is tried again on the next scan.
/// </summary>
/// <remarks>
/// It polls rather than using FileSystemWatcher: change events fire while a file is still being copied and
/// are lost on some network drives, while a scan that skips locked files is simple and testable.
/// </remarks>
public sealed partial class LocalInboxWatcher(
    ClaimIntakePipeline pipeline,
    IOptions<LocalInboxOptions> options,
    ILogger<LocalInboxWatcher> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);

    public const string ProcessedFolder = "processed";
    public const string FailedFolder = "failed";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        Directory.CreateDirectory(settings.Path);
        LogWatching(logger, System.IO.Path.GetFullPath(settings.Path));

        using var timer = new PeriodicTimer(settings.PollInterval);
        do
        {
            try
            {
                await ScanOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogScanFailed(logger, ex.GetType().Name);
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    /// <summary>Processes every document currently waiting in the folder. Returns how many reached the review store.</summary>
    public async Task<int> ScanOnceAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        Directory.CreateDirectory(settings.Path);

        var handedOff = 0;
        foreach (var path in Directory.EnumerateFiles(settings.Path).Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = System.IO.Path.GetFileName(path);
            if (name.StartsWith('.') || name.StartsWith('~'))
            {
                continue; // Editor and download temp files.
            }

            if (await TryProcessAsync(path, name, settings, cancellationToken))
            {
                handedOff++;
            }
        }

        return handedOff;
    }

    private async Task<bool> TryProcessAsync(string path, string name, LocalInboxOptions settings, CancellationToken cancellationToken)
    {
        var documentId = $"inbox/{name}";
        var safeId = ClaimIntakePipeline.Sanitise(documentId);

        if (!LocalPdfTextExtractor.SupportedExtensions.Contains(System.IO.Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
        {
            LogUnsupported(logger, safeId);
            Move(path, settings.Path, FailedFolder);
            return false;
        }

        BinaryData content;
        try
        {
            // An exclusive open fails while another process is still writing the file.
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            content = await BinaryData.FromStreamAsync(stream, cancellationToken);
        }
        catch (IOException)
        {
            return false; // Still being copied. Try again on the next scan.
        }

        try
        {
            // The file's creation time is when it reached the inbox: the fallback notification date (Milestone 4 date fix).
            var receivedAt = new DateTimeOffset(File.GetCreationTimeUtc(path), TimeSpan.Zero);
            await pipeline.ProcessAsync(new IntakeDocument(documentId, name, content, receivedAt), cancellationToken);
            _attempts.TryRemove(path, out _);
            Move(path, settings.Path, ProcessedFolder);
            return true;
        }
        catch (Exception ex) when (ex is LegacySystemUnavailableException or HandoffFailedException)
        {
            var attempts = _attempts.AddOrUpdate(path, 1, (_, n) => n + 1);
            if (attempts < settings.MaxAttempts)
            {
                LogWillRetry(logger, safeId, attempts, settings.MaxAttempts);
                return false;
            }

            _attempts.TryRemove(path, out _);
            Move(path, settings.Path, FailedFolder);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The pipeline has already logged the reason. The file will not get better by retrying.
            _attempts.TryRemove(path, out _);
            Move(path, settings.Path, FailedFolder);
            return false;
        }
    }

    private void Move(string path, string inbox, string folder)
    {
        var target = System.IO.Path.Combine(inbox, folder);
        Directory.CreateDirectory(target);
        var destination = System.IO.Path.Combine(target, System.IO.Path.GetFileName(path));
        if (File.Exists(destination))
        {
            var stem = System.IO.Path.GetFileNameWithoutExtension(path);
            var extension = System.IO.Path.GetExtension(path);
            destination = System.IO.Path.Combine(target, $"{stem}-{Guid.NewGuid():N}{extension}");
        }

        File.Move(path, destination);
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Watching {Folder} for claim documents.")]
    private static partial void LogWatching(ILogger logger, string folder);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scan failed: {ErrorType}. Will try again.")]
    private static partial void LogScanFailed(ILogger logger, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Document {DocumentId} has an unsupported file type and was moved to failed.")]
    private static partial void LogUnsupported(ILogger logger, string documentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Document {DocumentId} will be retried (attempt {Attempt} of {MaxAttempts}).")]
    private static partial void LogWillRetry(ILogger logger, string documentId, int attempt, int maxAttempts);
}
