using ClaimsIntake.Domain;

namespace ClaimsIntake.Api.Claims;

/// <summary>Where claims waiting for review are kept.</summary>
public interface IClaimRepository
{
    /// <summary>Lists claims, oldest first. Pass null for every status.</summary>
    Task<IReadOnlyList<Claim>> ListAsync(ClaimStatus? status, CancellationToken cancellationToken);

    Task<Claim?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Adds a new claim. Fails if a claim with the same id exists.</summary>
    Task AddAsync(Claim claim, CancellationToken cancellationToken);

    /// <summary>Persists changes made to a claim obtained from this repository.</summary>
    Task SaveAsync(Claim claim, CancellationToken cancellationToken);
}
