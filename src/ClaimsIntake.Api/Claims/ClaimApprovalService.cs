using System.Collections.Concurrent;
using System.Diagnostics;
using ClaimsIntake.Api.Legacy;
using ClaimsIntake.Api.Telemetry;
using ClaimsIntake.Domain;

namespace ClaimsIntake.Api.Claims;

public enum ApprovalOutcome
{
    /// <summary>The claim was approved and written to the legacy system by this call.</summary>
    Submitted,

    /// <summary>The claim had already been written. Nothing was sent again.</summary>
    AlreadySubmitted,

    NotFound,

    /// <summary>The claim's state or data-quality errors do not allow approval.</summary>
    NotAllowed,

    /// <summary>The claim lacks something the legacy system requires, such as a description.</summary>
    MissingLegacyData,

    /// <summary>The legacy system refused the claim. The claim stays approved.</summary>
    LegacyRefused,

    /// <summary>The legacy system could not be reached. The claim stays approved and the call can be repeated.</summary>
    LegacyUnavailable,
}

public sealed record ApprovalResult(ApprovalOutcome Outcome, Claim? Claim = null, string? Detail = null);

/// <summary>
/// Approves a claim and writes it to the legacy system, safely and repeatably.
/// </summary>
/// <remarks>
/// The legacy write uses the claim id as its idempotency key, and calls for one claim run one at a time.
/// So pressing Approve twice, a client retry, or a retry after a timeout cannot create a second legacy claim:
/// a claim that is already Submitted returns as it is, and one stuck at Approved (the legacy system was
/// down) is sent again under the same key.
/// </remarks>
public sealed class ClaimApprovalService(
    IClaimRepository repository,
    ILegacyClaimWriter legacy,
    TimeProvider time,
    ILogger<ClaimApprovalService> logger)
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async Task<ApprovalResult> ApproveAsync(Guid id, string reviewer, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var claim = await repository.GetAsync(id, cancellationToken);
            if (claim is null)
            {
                return new ApprovalResult(ApprovalOutcome.NotFound);
            }

            switch (claim.Status)
            {
                case ClaimStatus.Submitted:
                    return new ApprovalResult(ApprovalOutcome.AlreadySubmitted, claim);

                case ClaimStatus.Rejected:
                    return new ApprovalResult(ApprovalOutcome.NotAllowed, claim, "The claim cannot be approved because it is already Rejected.");

                case ClaimStatus.NeedsReview:
                    // Check the legacy system can take the claim before the decision is recorded.
                    if (!LegacyClaimMapper.TryMap(claim, out _, out var problems))
                    {
                        return new ApprovalResult(ApprovalOutcome.MissingLegacyData, claim, string.Join(" ", problems));
                    }

                    try
                    {
                        claim.Approve(reviewer, time.GetUtcNow());
                    }
                    catch (InvalidClaimTransitionException ex)
                    {
                        return new ApprovalResult(ApprovalOutcome.NotAllowed, claim, ex.Message);
                    }

                    await repository.SaveAsync(claim, cancellationToken);
                    ApiMetrics.Decisions.Add(1, new KeyValuePair<string, object?>("decision", "approved"));
                    logger.LogInformation("Claim {ClaimId} approved", claim.Id);
                    break;
            }

            // Status is Approved: either just now, or earlier when the legacy write did not complete.
            return await SubmitAsync(claim, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ApprovalResult> SubmitAsync(Claim claim, CancellationToken cancellationToken)
    {
        using var activity = ApiTelemetry.Source.StartActivity("claim.submit_to_legacy");
        activity?.SetTag("claimId", claim.Id.ToString("D"));

        try
        {
            var submission = await legacy.SubmitAsync(claim, cancellationToken);
            claim.MarkSubmitted(submission.Reference);
            await repository.SaveAsync(claim, cancellationToken);

            var outcome = submission.Replayed ? "replayed" : "created";
            ApiMetrics.LegacySubmissions.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
            activity?.SetTag("outcome", outcome);
            logger.LogInformation("Claim {ClaimId} written to the legacy system as {LegacyReference} ({Outcome})",
                claim.Id, submission.Reference, outcome);
            return new ApprovalResult(ApprovalOutcome.Submitted, claim);
        }
        catch (LegacyRejectedException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "refused");
            ApiMetrics.LegacySubmissions.Add(1, new KeyValuePair<string, object?>("outcome", "refused"));
            logger.LogWarning("Claim {ClaimId} was refused by the legacy system", claim.Id);
            return new ApprovalResult(ApprovalOutcome.LegacyRefused, claim, ex.Message);
        }
        catch (LegacyUnavailableException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "unavailable");
            ApiMetrics.LegacySubmissions.Add(1, new KeyValuePair<string, object?>("outcome", "unavailable"));
            logger.LogWarning("Claim {ClaimId} could not be written to the legacy system: {ErrorType}", claim.Id, ex.GetType().Name);
            return new ApprovalResult(ApprovalOutcome.LegacyUnavailable, claim,
                "The claims system could not be reached. The claim is approved; try again shortly. A retry cannot create a duplicate.");
        }
    }
}
