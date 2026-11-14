using System.Net.Http.Json;
using Azure.Messaging.ServiceBus;
using ClaimsIntake.Domain.Intake;

namespace ClaimsIntake.Pipeline.Handoff;

/// <summary>Where the pipeline puts a proposed claim for a handler. Implementations must be safe to call twice for one document.</summary>
public interface IClaimSink
{
    Task HandOffAsync(ProposedClaim claim, CancellationToken cancellationToken);
}

/// <summary>The hand-off failed. Retrying may help.</summary>
public sealed class HandoffFailedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Local mode: POSTs the proposed claim to the review API (ADR 0005). The API answers 201 for a new claim
/// and 200 when it already had that document, so a retry is harmless.
/// </summary>
public sealed class HttpClaimSink(HttpClient client) : IClaimSink
{
    public async Task HandOffAsync(ProposedClaim claim, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        try
        {
            using var response = await client.PostAsJsonAsync("intake/claims", claim, ProposedClaim.JsonOptions, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HandoffFailedException($"The review API refused the claim (HTTP {(int)response.StatusCode}).");
            }
        }
        catch (HttpRequestException ex)
        {
            throw new HandoffFailedException($"The review API could not be reached ({ex.GetType().Name}).", ex);
        }
    }
}

/// <summary>
/// Azure mode: sends the proposed claim to the claims-for-review Service Bus queue. The message id is the
/// claim id, so a queue with duplicate detection drops a repeat, and the API ignores one it has seen.
/// Compiled and unit-tested against a fake sender only.
/// </summary>
public sealed class ServiceBusClaimSink(ServiceBusSender sender) : IClaimSink
{
    public const string Subject = "claim-proposed";

    public async Task HandOffAsync(ProposedClaim claim, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var message = new ServiceBusMessage(BinaryData.FromString(claim.ToJson()))
        {
            MessageId = claim.ClaimId.ToString("D"),
            ContentType = "application/json",
            Subject = Subject,
        };

        try
        {
            await sender.SendMessageAsync(message, cancellationToken);
        }
        catch (ServiceBusException ex)
        {
            throw new HandoffFailedException($"Service Bus refused the claim ({ex.Reason}).", ex);
        }
    }
}
