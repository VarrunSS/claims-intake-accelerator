using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using ClaimsIntake.Domain;

namespace ClaimsIntake.Api.Legacy;

/// <summary>The legacy system's answer to a submission.</summary>
/// <param name="Reference">The legacy claim reference, for example LC-000001.</param>
/// <param name="Replayed">True when the legacy system had already stored this claim under the same key.</param>
public sealed record LegacySubmission(string Reference, bool Replayed);

/// <summary>Writes an approved claim to the legacy claims system. This is the only write path (ADR 0002).</summary>
public interface ILegacyClaimWriter
{
    /// <summary>
    /// Creates the claim in the legacy system. The idempotency key is the claim id, so calling this again for
    /// the same claim, after a timeout or a crash, returns the original reference and creates nothing new.
    /// </summary>
    Task<LegacySubmission> SubmitAsync(Claim claim, CancellationToken cancellationToken);
}

/// <summary>The legacy system refused the claim (4xx). Retrying the same claim will not help.</summary>
public sealed class LegacyRejectedException(string message) : Exception(message);

/// <summary>The legacy system could not be reached or failed (5xx, timeout). Retrying the same call is safe.</summary>
public sealed class LegacyUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Posts to the legacy system over a typed <see cref="HttpClient"/>. The base address comes from configuration.</summary>
public sealed class LegacyClaimWriter(HttpClient client) : ILegacyClaimWriter
{
    public const string IdempotencyHeader = "Idempotency-Key";
    public const string ReplayHeader = "Idempotent-Replay";

    private sealed record CreatedDto(string Reference);

    public async Task<LegacySubmission> SubmitAsync(Claim claim, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (!LegacyClaimMapper.TryMap(claim, out var request, out var problems))
        {
            throw new LegacyRejectedException($"The claim cannot be sent to the legacy system: {string.Join(" ", problems)}");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, "claims") { Content = JsonContent.Create(request) };
        message.Headers.Add(IdempotencyHeader, claim.Id.ToString("D"));

        try
        {
            using var response = await client.SendAsync(message, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK)
            {
                var created = await response.Content.ReadFromJsonAsync<CreatedDto>(cancellationToken);
                return string.IsNullOrWhiteSpace(created?.Reference)
                    ? throw new LegacyUnavailableException("The legacy system answered without a claim reference.")
                    : new LegacySubmission(created.Reference, response.Headers.Contains(ReplayHeader));
            }

            // Only the status is reported: the body may echo claim details.
            throw (int)response.StatusCode is >= 400 and < 500 and not 408 and not 429
                ? new LegacyRejectedException($"The legacy system refused the claim (HTTP {(int)response.StatusCode}).")
                : new LegacyUnavailableException($"The legacy system failed (HTTP {(int)response.StatusCode}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
                                    && !cancellationToken.IsCancellationRequested)
        {
            throw new LegacyUnavailableException($"The legacy system could not be reached ({ex.GetType().Name}).", ex);
        }
    }
}

/// <summary>Turns an extracted claim into the body the legacy system expects.</summary>
public static class LegacyClaimMapper
{
    public sealed record Request(
        string PolicyNumber, string ClaimantName, string LossDate, string Description, decimal? EstimatedAmountGbp);

    public static bool TryMap(Claim claim, out Request request, out IReadOnlyList<string> problems)
    {
        var found = new List<string>();
        var policy = Value(claim, ClaimFields.PolicyNumber);
        var claimant = Value(claim, ClaimFields.ClaimantName);
        var description = Value(claim, ClaimFields.LossDescription);

        if (policy is null)
        {
            found.Add("The policy number is missing.");
        }

        if (claimant is null)
        {
            found.Add("The claimant name is missing.");
        }

        if (description is null)
        {
            found.Add("The description of the loss is missing.");
        }

        DateOnly lossDate = default;
        if (!ClaimValidator.TryParseDate(Value(claim, ClaimFields.LossDate), out lossDate))
        {
            found.Add("The loss date is missing or cannot be read.");
        }

        problems = found;
        request = found.Count > 0
            ? new Request("", "", "", "", null)
            : new Request(policy!, claimant!, lossDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), description!, ParseAmount(Value(claim, ClaimFields.EstimatedAmount)));
        return found.Count == 0;
    }

    private static string? Value(Claim claim, string name) =>
        claim.Fields.TryGetValue(name, out var field) && field.HasValue ? field.Value!.Trim() : null;

    private static decimal? ParseAmount(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var cleaned = text.Replace("£", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal).Trim();
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) && amount >= 0 ? amount : null;
    }
}
