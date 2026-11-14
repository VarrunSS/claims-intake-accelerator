using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaimsIntake.Legacy;

/// <summary>Body of POST /claims.</summary>
public sealed record CreateClaimRequest(
    string? PolicyNumber,
    string? ClaimantName,
    DateOnly? LossDate,
    string? Description,
    decimal? EstimatedAmountGbp);

/// <summary>A claim as the legacy system stores it.</summary>
public sealed record LegacyClaim(
    string Reference,
    string PolicyNumber,
    string ClaimantName,
    DateOnly LossDate,
    string Description,
    decimal? EstimatedAmountGbp,
    DateTimeOffset CreatedAt);

/// <summary>The result of trying to store a claim under an idempotency key.</summary>
public abstract record CreateOutcome
{
    public sealed record Created(LegacyClaim Claim) : CreateOutcome;

    /// <summary>The same key and the same request were seen before; the original claim is returned.</summary>
    public sealed record Replayed(LegacyClaim Claim) : CreateOutcome;

    /// <summary>The key was used before with a different request.</summary>
    public sealed record KeyReuse : CreateOutcome;
}

public interface ILegacyClaimStore
{
    CreateOutcome Create(string idempotencyKey, CreateClaimRequest request, LegacyPolicy policy);

    LegacyClaim? Find(string reference);

    /// <summary>The claims held for a policy, oldest first. Read-only; used by the duplicate check.</summary>
    IReadOnlyList<LegacyClaim> ForPolicy(string policyNumber);
}

/// <summary>
/// Stores claims in memory. A retry with the same Idempotency-Key and body returns the first result and
/// creates nothing new. The same key with a different body is refused.
/// </summary>
public sealed class InMemoryLegacyClaimStore(TimeProvider time) : ILegacyClaimStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string BodyHash, LegacyClaim Claim)> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LegacyClaim> _byReference = new(StringComparer.Ordinal);
    private int _nextNumber = 1;

    public CreateOutcome Create(string idempotencyKey, CreateClaimRequest request, LegacyPolicy policy)
    {
        var bodyHash = Hash(request);

        lock (_gate)
        {
            if (_byKey.TryGetValue(idempotencyKey, out var existing))
            {
                return existing.BodyHash == bodyHash
                    ? new CreateOutcome.Replayed(existing.Claim)
                    : new CreateOutcome.KeyReuse();
            }

            var claim = new LegacyClaim(
                $"LC-{_nextNumber++:D6}",
                policy.Number,
                request.ClaimantName!.Trim(),
                request.LossDate!.Value,
                request.Description!.Trim(),
                request.EstimatedAmountGbp,
                time.GetUtcNow());

            _byKey[idempotencyKey] = (bodyHash, claim);
            _byReference[claim.Reference] = claim;
            return new CreateOutcome.Created(claim);
        }
    }

    public LegacyClaim? Find(string reference)
    {
        lock (_gate)
        {
            return _byReference.GetValueOrDefault(reference);
        }
    }

    public IReadOnlyList<LegacyClaim> ForPolicy(string policyNumber)
    {
        var number = policyNumber.Trim();
        lock (_gate)
        {
            return [.. _byReference.Values
                .Where(c => c.PolicyNumber.Equals(number, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.CreatedAt).ThenBy(c => c.Reference, StringComparer.Ordinal)];
        }
    }

    private static string Hash(CreateClaimRequest request)
    {
        // Normalise the policy number so "tm-cp-100001 " and "TM-CP-100001" count as the same request.
        var normalised = request with { PolicyNumber = request.PolicyNumber?.Trim().ToUpperInvariant() };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(normalised))));
    }
}
