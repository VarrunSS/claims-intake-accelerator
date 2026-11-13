using System.Collections.Concurrent;
using ClaimsIntake.Domain;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ClaimsIntake.Api.Claims;

/// <summary>Holds claims in memory. Everything is lost on restart, which is fine for the skeleton.</summary>
public sealed class InMemoryClaimRepository : IClaimRepository
{
    private readonly ConcurrentDictionary<Guid, Claim> _claims = new();

    public Task<IReadOnlyList<Claim>> ListAsync(ClaimStatus? status, CancellationToken cancellationToken)
    {
        IReadOnlyList<Claim> result = _claims.Values
            .Where(c => status is null || c.Status == status)
            .OrderBy(c => c.ReceivedAt)
            .ThenBy(c => c.Id)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<Claim?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(_claims.GetValueOrDefault(id));

    public Task AddAsync(Claim claim, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (!_claims.TryAdd(claim.Id, claim))
        {
            throw new InvalidOperationException($"A claim with id {claim.Id} already exists.");
        }

        return Task.CompletedTask;
    }

    // Claims are held by reference, so changes are already visible. A durable store would write here.
    public Task SaveAsync(Claim claim, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Reports ready once the repository can be read. A real store would check its connection here.</summary>
public sealed class ClaimRepositoryHealthCheck(IClaimRepository repository) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await repository.ListAsync(ClaimStatus.NeedsReview, cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("The claim repository could not be read.", ex);
        }
    }
}
