using Azure.Storage.Blobs;
using ClaimsIntake.Pipeline;
using ClaimsIntake.Pipeline.Text;
using Microsoft.Azure.Functions.Worker;

namespace ClaimsIntake.Functions;

/// <summary>
/// Fires when a document lands in the inbox container and runs it through <see cref="ClaimIntakePipeline"/>:
/// extract text, extract fields, look up the policy, validate, then hand the proposed claim to the review
/// queue. The Function holds no intake logic of its own, so the same code runs locally in the inbox watcher.
/// </summary>
/// <remarks>
/// Connections use identity-based settings: InboxStorage__serviceUri for the trigger, and the Intake__*
/// settings for the pipeline, with AZURE_CLIENT_ID or Intake__ManagedIdentityClientId selecting the
/// user-assigned managed identity. No keys or connection strings. An exception makes the blob trigger retry
/// and finally poison the blob, which keeps a failed document visible rather than dropped (ADR 0001).
/// Compiled and unit-tested with fakes only; the Function host has never been started.
/// </remarks>
public sealed class ClaimDocumentReceived(ClaimIntakePipeline pipeline)
{
    [Function(nameof(ClaimDocumentReceived))]
    public async Task Run(
        [BlobTrigger("inbox/{name}", Source = BlobTriggerSource.EventGrid, Connection = "InboxStorage")] BlobClient blob,
        string name,
        CancellationToken cancellationToken)
    {
        // The blob's creation time is when the document reached us: the fallback notification date (Milestone 4 date fix).
        var properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
        await using var stream = await blob.OpenReadAsync(cancellationToken: cancellationToken);
        await Process(stream, name, properties.Value.CreatedOn, cancellationToken);
    }

    /// <summary>Reads the stream and runs the document through the pipeline. Split from <see cref="Run"/> so it can be tested without a blob.</summary>
    public async Task Process(Stream document, string name, DateTimeOffset? receivedAt, CancellationToken cancellationToken)
    {
        var content = await ReadLimitedAsync(document, cancellationToken);
        await pipeline.ProcessAsync(new IntakeDocument($"inbox/{name}", name, content, receivedAt), cancellationToken);
    }

    /// <summary>
    /// Reads the blob, refusing anything larger than <see cref="ClaimIntakePipeline.HardLimitBytes"/> before it is all in memory.
    /// A document between <see cref="ClaimIntakePipeline.HardLimitBytes"/> and the hard limit is read and then handed to a
    /// person by the pipeline's size check; beyond the hard limit the blob is poisoned and stays visible (ADR 0001).
    /// </summary>
    public static async Task<BinaryData> ReadLimitedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > ClaimIntakePipeline.HardLimitBytes)
            {
                throw new UnreadableDocumentException(
                    $"The document is larger than {ClaimIntakePipeline.HardLimitBytes / (1024 * 1024)} MB.");
            }

            buffer.Write(chunk, 0, read);
        }

        return new BinaryData(buffer.ToArray());
    }
}
