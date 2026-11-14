using System.Collections.Concurrent;
using System.Diagnostics;
using ClaimsIntake.Api.Legacy;
using ClaimsIntake.Api.Telemetry;
using ClaimsIntake.Domain;
using ClaimsIntake.Domain.Assessment;
using ClaimsIntake.Domain.Security;

namespace ClaimsIntake.Api.Claims;

public enum ApprovalOutcome
{
    /// <summary>The claim was approved and written to the legacy system by this call.</summary>
    Submitted,

    /// <summary>The claim had already been written. Nothing was sent again.</summary>
    AlreadySubmitted,

    /// <summary>The claim was rejected by this call.</summary>
    Rejected,

    NotFound,

    /// <summary>The claim's state or data-quality errors do not allow the decision.</summary>
    NotAllowed,

    /// <summary>The caller does not hold the role the claim's route requires (segregation of duties, ADR 0009).</summary>
    Forbidden,

    /// <summary>The claim lacks something the legacy system requires, such as a description.</summary>
    MissingLegacyData,

    /// <summary>The legacy system refused the claim. The claim stays approved.</summary>
    LegacyRefused,

    /// <summary>The legacy system could not be reached. The claim stays approved and the call can be repeated.</summary>
    LegacyUnavailable,
}

public sealed record ApprovalResult(ApprovalOutcome Outcome, Claim? Claim = null, string? Detail = null);

/// <summary>
/// Approves or rejects a claim, safely and repeatably. Both decisions run under the same per-claim gate (REL-03), so an
/// approval that is writing to the legacy system and a rejection that arrives meanwhile cannot interleave: the rejection
/// waits, then finds the claim already decided and is refused with a conflict.
/// </summary>
/// <remarks>
/// The legacy write uses the claim id as its idempotency key, so pressing Approve twice, a client retry, or a retry after a
/// timeout cannot create a second legacy claim: a claim that is already Submitted returns as it is, and one stuck at Approved
/// (the legacy system was down) is sent again under the same key.
/// Every decision is audited (SEC-04): approved, rejected, submitted and submit_failed, with the actor's object id. The
/// approval is written before the legacy call. The entries have deterministic keys, so a retry that finds the claim already
/// decided writes the same rows again (an upsert) and so repairs a record that an earlier failure left out, without duplicating.
/// </remarks>
public sealed class ClaimDecisionService(
    IClaimRepository repository,
    ILegacyClaimWriter legacy,
    IAuditLog audit,
    TimeProvider time,
    ILogger<ClaimDecisionService> logger)
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async Task<ApprovalResult> ApproveAsync(Guid id, Approver approver, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approver);
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var claim = await repository.GetAsync(id, cancellationToken);
            if (claim is null)
            {
                return new ApprovalResult(ApprovalOutcome.NotFound);
            }

            // Segregation of duties first, whatever the state: a person who may not approve this claim learns nothing else.
            if (!claim.MayApprove(approver))
            {
                logger.LogInformation("Claim {ClaimId} approval refused: the caller lacks the {Role} role", claim.Id, claim.RequiredApprovalRole);
                return new ApprovalResult(ApprovalOutcome.Forbidden, claim, ForbiddenDetail(claim));
            }

            switch (claim.Status)
            {
                case ClaimStatus.Submitted:
                    await EnsureDecisionAuditAsync(claim, cancellationToken);
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
                        claim.Approve(approver, time.GetUtcNow());
                    }
                    catch (InsufficientRoleException)
                    {
                        return new ApprovalResult(ApprovalOutcome.Forbidden, claim, ForbiddenDetail(claim));
                    }
                    catch (InvalidClaimTransitionException ex)
                    {
                        return new ApprovalResult(ApprovalOutcome.NotAllowed, claim, ex.Message);
                    }

                    await audit.AppendAsync(DecisionAudit.ForApproved(claim), cancellationToken);
                    await repository.SaveAsync(claim, cancellationToken);
                    ApiMetrics.Decisions.Add(1, new KeyValuePair<string, object?>("decision", "approved"));
                    logger.LogInformation("Claim {ClaimId} approved", claim.Id);
                    break;
            }

            // Status is Approved: either just now, or earlier when the legacy write did not complete. The approval record is
            // written (again, harmlessly) before the legacy call, so a write is never made without its approval on record.
            await audit.AppendAsync(DecisionAudit.ForApproved(claim), cancellationToken);
            return await SubmitAsync(claim, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ApprovalResult> RejectAsync(Guid id, Approver approver, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approver);
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var claim = await repository.GetAsync(id, cancellationToken);
            if (claim is null)
            {
                return new ApprovalResult(ApprovalOutcome.NotFound);
            }

            try
            {
                claim.Reject(approver, reason, time.GetUtcNow());
            }
            catch (InsufficientRoleException ex)
            {
                return new ApprovalResult(ApprovalOutcome.Forbidden, claim, ex.Message);
            }
            catch (InvalidClaimTransitionException ex)
            {
                return new ApprovalResult(ApprovalOutcome.NotAllowed, claim, ex.Message);
            }

            await audit.AppendAsync(DecisionAudit.ForRejected(claim), cancellationToken);
            await repository.SaveAsync(claim, cancellationToken);
            ApiMetrics.Decisions.Add(1, new KeyValuePair<string, object?>("decision", "rejected"));
            logger.LogInformation("Claim {ClaimId} rejected", claim.Id);
            return new ApprovalResult(ApprovalOutcome.Rejected, claim);
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
            claim.MarkSubmitted(submission.Reference, time.GetUtcNow());
            await repository.SaveAsync(claim, cancellationToken);
            await audit.AppendAsync(DecisionAudit.ForSubmitted(claim), cancellationToken);

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
            await audit.AppendAsync(DecisionAudit.ForSubmitFailed(claim, "refused", time.GetUtcNow()), cancellationToken);
            return new ApprovalResult(ApprovalOutcome.LegacyRefused, claim, ex.Message);
        }
        catch (LegacyUnavailableException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "unavailable");
            ApiMetrics.LegacySubmissions.Add(1, new KeyValuePair<string, object?>("outcome", "unavailable"));
            logger.LogWarning("Claim {ClaimId} could not be written to the legacy system: {ErrorType}", claim.Id, ex.GetType().Name);
            await audit.AppendAsync(DecisionAudit.ForSubmitFailed(claim, "unavailable", time.GetUtcNow()), cancellationToken);
            return new ApprovalResult(ApprovalOutcome.LegacyUnavailable, claim,
                "The claims system could not be reached. The claim is approved; try again shortly. A retry cannot create a duplicate.");
        }
    }

    /// <summary>Writes the approved and submitted records again, so a record lost to an earlier failure is repaired. Same keys, so no duplicates.</summary>
    private async Task EnsureDecisionAuditAsync(Claim claim, CancellationToken cancellationToken)
    {
        await audit.AppendAsync(DecisionAudit.ForApproved(claim), cancellationToken);
        await audit.AppendAsync(DecisionAudit.ForSubmitted(claim), cancellationToken);
    }

    private static string ForbiddenDetail(Claim claim) =>
        claim.RequiredApprovalRole == AppRoles.SeniorHandler
            ? "This claim is routed to senior review. Only a senior handler can approve it. A claims handler can still reject it."
            : "You need the ClaimsHandler or SeniorHandler role to approve a claim.";
}
