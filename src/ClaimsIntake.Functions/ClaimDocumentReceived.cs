using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace ClaimsIntake.Functions;

/// <summary>Message put on the claim-documents queue for the next stage to pick up.</summary>
public sealed record ClaimDocumentMessage(Guid MessageId, string BlobName, long SizeBytes, DateTimeOffset ReceivedAt);

/// <summary>
/// Milestone 1 stub. Fires when a document lands in the inbox container, logs it and puts a message on
/// the queue. Document Intelligence and the extraction agent are added in Milestone 2.
/// </summary>
/// <remarks>
/// Connections use identity-based settings: InboxStorage__serviceUri and ServiceBus__fullyQualifiedNamespace,
/// with AZURE_CLIENT_ID selecting the user-assigned managed identity. No keys or connection strings.
/// </remarks>
public sealed partial class ClaimDocumentReceived(ILogger<ClaimDocumentReceived> logger, TimeProvider time)
{
    [Function(nameof(ClaimDocumentReceived))]
    [ServiceBusOutput("%ClaimDocumentsQueue%", Connection = "ServiceBus")]
    public string Run(
        [BlobTrigger("inbox/{name}", Source = BlobTriggerSource.EventGrid, Connection = "InboxStorage")] Stream document,
        string name)
    {
        var message = BuildMessage(name, document.CanSeek ? document.Length : -1, time.GetUtcNow());

        // The blob name comes from whoever uploaded the file, so it is cleaned before it is logged.
        LogReceived(logger, message.MessageId, Sanitise(name), message.SizeBytes);

        return JsonSerializer.Serialize(message);
    }

    public static ClaimDocumentMessage BuildMessage(string blobName, long sizeBytes, DateTimeOffset receivedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);
        return new ClaimDocumentMessage(Guid.NewGuid(), blobName, sizeBytes, receivedAt);
    }

    /// <summary>Removes control characters (including line breaks) and caps the length of a name for logging.</summary>
    public static string Sanitise(string value)
    {
        var cleaned = ControlCharacters().Replace(value, "_");
        return cleaned.Length <= 200 ? cleaned : cleaned[..200];
    }

    [GeneratedRegex(@"[\p{C}]")]
    private static partial Regex ControlCharacters();

    [LoggerMessage(Level = LogLevel.Information, Message = "Claim document {MessageId} received: {BlobName} ({SizeBytes} bytes). Queued for extraction.")]
    private static partial void LogReceived(ILogger logger, Guid messageId, string blobName, long sizeBytes);
}
