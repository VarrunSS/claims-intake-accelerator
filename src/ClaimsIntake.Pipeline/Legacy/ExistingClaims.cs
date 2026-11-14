using System.Net.Http.Json;
using ClaimsIntake.Domain;

namespace ClaimsIntake.Pipeline.Legacy;

/// <summary>A claim that already exists, in the review store or the legacy system. Only what the duplicate check needs.</summary>
/// <param name="Reference">The review-store claim id, or the legacy reference.</param>
/// <param name="Source"><c>review</c> or <c>legacy</c>.</param>
/// <param name="PolicyNumber">The policy the claim is on.</param>
/// <param name="LossDate">The date of loss, when known.</param>
/// <param name="AmountGbp">The estimated amount, when known.</param>
/// <param name="Status">The claim's status in its own system.</param>
public sealed record ExistingClaim(
    string Reference, string Source, string PolicyNumber, DateOnly? LossDate, decimal? AmountGbp, string Status);

/// <summary>The claims already held for a policy. Read-only: nothing here can create or change a claim.</summary>
public interface IExistingClaims
{
    Task<IReadOnlyList<ExistingClaim>> FindByPolicyAsync(string policyNumber, CancellationToken cancellationToken);
}

/// <summary>Reads the claims the legacy system holds for a policy (<c>GET /claims?policyNumber=</c>).</summary>
public sealed class LegacyExistingClaims(HttpClient client) : IExistingClaims
{
    private sealed record LegacyClaimDto(string Reference, string PolicyNumber, DateOnly LossDate, decimal? EstimatedAmountGbp);

    public async Task<IReadOnlyList<ExistingClaim>> FindByPolicyAsync(string policyNumber, CancellationToken cancellationToken)
    {
        try
        {
            var claims = await client.GetFromJsonAsync<List<LegacyClaimDto>>(
                $"claims?policyNumber={Uri.EscapeDataString(policyNumber)}", cancellationToken) ?? [];
            return [.. claims.Select(c => new ExistingClaim(c.Reference, "legacy", c.PolicyNumber, c.LossDate, c.EstimatedAmountGbp, "Submitted"))];
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            throw new LegacySystemUnavailableException($"The legacy claims lookup failed ({ex.GetType().Name}).", ex);
        }
    }
}

/// <summary>Reads the claims in the review store for a policy (<c>GET /claims/history?policyNumber=</c> on the API).</summary>
public sealed class ReviewStoreExistingClaims(HttpClient client) : IExistingClaims
{
    private sealed record HistoryDto(Guid Id, string PolicyNumber, DateOnly? LossDate, decimal? AmountGbp, string Status);

    public async Task<IReadOnlyList<ExistingClaim>> FindByPolicyAsync(string policyNumber, CancellationToken cancellationToken)
    {
        try
        {
            var claims = await client.GetFromJsonAsync<List<HistoryDto>>(
                $"claims/history?policyNumber={Uri.EscapeDataString(policyNumber)}", ClaimsJson.Options, cancellationToken) ?? [];
            return [.. claims.Select(c => new ExistingClaim(c.Id.ToString(), "review", c.PolicyNumber, c.LossDate, c.AmountGbp, c.Status))];
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            throw new LegacySystemUnavailableException($"The review store lookup failed ({ex.GetType().Name}).", ex);
        }
    }
}

/// <summary>Asks several sources and merges the answers. If one cannot be reached the whole lookup fails: a duplicate may be hiding there.</summary>
public sealed class CompositeExistingClaims(IEnumerable<IExistingClaims> sources) : IExistingClaims
{
    private readonly IExistingClaims[] _sources = [.. sources];

    public async Task<IReadOnlyList<ExistingClaim>> FindByPolicyAsync(string policyNumber, CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(_sources.Select(s => s.FindByPolicyAsync(policyNumber, cancellationToken)));
        return [.. results.SelectMany(r => r)];
    }
}

internal static class ClaimsJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web);
}
