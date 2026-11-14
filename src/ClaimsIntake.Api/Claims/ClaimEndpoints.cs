using System.Security.Claims;
using Claim = ClaimsIntake.Domain.Claim;
using ClaimsIntake.Domain.Security;
using ClaimsIntake.Security.AspNetCore;
using ClaimsIntake.Domain;
using ClaimsIntake.Domain.Assessment;

namespace ClaimsIntake.Api.Claims;

public static class ClaimEndpoints
{
    private const int MaxTextLength = 500;

    public static IEndpointRouteBuilder MapClaimEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/me", Me).RequireAuthorization(Policies.Handler).WithTags("Claims");

        // Every claim route needs a signed-in person with a handler role. /history is the exception: it is for the pipeline's
        // app-only identity and refuses user tokens.
        app.MapGet("/claims/history", History).RequireAuthorization(Policies.ReadHistory).WithTags("Claims");

        var group = app.MapGroup("/claims").WithTags("Claims").RequireAuthorization(Policies.Handler);

        group.MapGet("/", ListClaims);
        group.MapGet("/{id:guid}", GetClaim);
        group.MapGet("/{id:guid}/audit", GetAudit);
        group.MapPost("/{id:guid}/approve", ApproveClaim);
        group.MapPost("/{id:guid}/reject", RejectClaim);

        return app;
    }

    // GET /claims?status=NeedsReview   (default), or Approved, Rejected, Submitted, or all.
    private static async Task<IResult> ListClaims(string? status, IClaimRepository repository, CancellationToken ct)
    {
        ClaimStatus? filter;
        if (string.IsNullOrWhiteSpace(status))
        {
            filter = ClaimStatus.NeedsReview;
        }
        else if (status.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            filter = null;
        }
        else if (Enum.TryParse<ClaimStatus>(status, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
        {
            filter = parsed;
        }
        else
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["status"] = [$"Use one of: {string.Join(", ", Enum.GetNames<ClaimStatus>())}, all."],
            });
        }

        var claims = await repository.ListAsync(filter, ct);
        return Results.Ok(claims.Select(ClaimSummary.From));
    }

    // GET /claims/history?policyNumber=TM-CP-100001: the claims on a policy that could still be duplicates (rejected ones are left out).
    // It returns ids, dates, amounts and statuses only. The intake pipeline calls it for the duplicate check.
    private static async Task<IResult> History(string? policyNumber, IClaimRepository repository, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(policyNumber) || policyNumber.Length > 40)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["policyNumber"] = ["A policy number is required."] });
        }

        var claims = await repository.ListAsync(null, ct);
        return Results.Ok(claims
            .Where(c => c.Status != ClaimStatus.Rejected && string.Equals(c.PolicyNumber, policyNumber.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(ClaimHistoryItem.From));
    }

    // GET /claims/{id}/audit: every tool call made while the claim was assessed, oldest first. Arguments are redacted.
    private static async Task<IResult> GetAudit(Guid id, IClaimRepository repository, IAuditLog audit, CancellationToken ct)
    {
        if (await repository.GetAsync(id, ct) is null)
        {
            return NotFound(id);
        }

        return Results.Ok((await audit.ForClaimAsync(id, ct)).Select(AuditEntryDto.From));
    }

    private static async Task<IResult> GetClaim(Guid id, IClaimRepository repository, CancellationToken ct)
    {
        var claim = await repository.GetAsync(id, ct);
        return claim is null ? NotFound(id) : Results.Ok(ClaimDetail.From(claim));
    }

    // Approving also writes to the legacy system, so it can be repeated: an approved claim whose write failed is
    // sent again, and one that was already written is returned as it is, with no second legacy claim.
    // The reviewer is the signed-in caller (the token's oid), never anything in the body.
    private static async Task<IResult> ApproveClaim(
        Guid id,
        ApproveRequest? request,
        ClaimsPrincipal user,
        ClaimDecisionService decisions,
        HttpResponse response,
        CancellationToken ct)
    {
        if (RejectReviewerInBody(request?.Reviewer) is { } problem)
        {
            return problem;
        }

        if (user.ToApprover() is not { } approver)
        {
            return NoIdentity();
        }

        var result = await decisions.ApproveAsync(id, approver, ct);
        switch (result.Outcome)
        {
            case ApprovalOutcome.Submitted:
                return Results.Ok(ClaimDetail.From(result.Claim!));
            case ApprovalOutcome.AlreadySubmitted:
                response.Headers["Idempotent-Replay"] = "true";
                return Results.Ok(ClaimDetail.From(result.Claim!));
            case ApprovalOutcome.NotFound:
                return NotFound(id);
            case ApprovalOutcome.Forbidden:
                return Forbidden(result.Claim!, result.Detail!);
            case ApprovalOutcome.NotAllowed:
                return Conflict(result.Detail!);
            case ApprovalOutcome.MissingLegacyData:
                return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "Claim incomplete for the claims system", detail: result.Detail);
            case ApprovalOutcome.LegacyRefused:
                return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "The claims system refused the claim", detail: result.Detail);
            default:
                return Results.Problem(statusCode: StatusCodes.Status502BadGateway,
                    title: "The claims system is unavailable", detail: result.Detail);
        }
    }

    private static async Task<IResult> RejectClaim(
        Guid id,
        RejectRequest? request,
        ClaimsPrincipal user,
        ClaimDecisionService decisions,
        CancellationToken ct)
    {
        if (RejectReviewerInBody(request?.Reviewer) is { } problem)
        {
            return problem;
        }

        var errors = ValidateReason(request?.Reason);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        if (user.ToApprover() is not { } approver)
        {
            return NoIdentity();
        }

        var result = await decisions.RejectAsync(id, approver, request!.Reason!, ct);
        return result.Outcome switch
        {
            ApprovalOutcome.Rejected => Results.Ok(ClaimDetail.From(result.Claim!)),
            ApprovalOutcome.NotFound => NotFound(id),
            ApprovalOutcome.Forbidden => Forbidden(result.Claim!, result.Detail!),
            _ => Conflict(result.Detail!),
        };
    }

    // GET /me: who the token says the caller is. The review UI shows it and uses the roles to explain what is possible.
    private static IResult Me(ClaimsPrincipal user) =>
        user.ToApprover() is { } who
            ? Results.Ok(new MeDto(who.Id, who.DisplayName ?? who.Id, who.Roles, who.IsSenior))
            : NoIdentity();

    // Old clients sent the reviewer in the body. That is refused loudly rather than ignored (SEC-02).
    private static IResult? RejectReviewerInBody(string? reviewer) =>
        reviewer is null
            ? null
            : Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["reviewer"] = ["The reviewer is taken from your sign-in. Remove \"reviewer\" from the request."],
            });

    private static Dictionary<string, string[]> ValidateReason(string? reason)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(reason))
        {
            errors["reason"] = ["A reason is required to reject a claim."];
        }
        else if (reason.Length > MaxTextLength)
        {
            errors["reason"] = [$"The reason must be at most {MaxTextLength} characters."];
        }

        return errors;
    }

    private static IResult Forbidden(Claim claim, string detail) =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden,
            title: claim.RequiredApprovalRole == AppRoles.SeniorHandler ? "Senior review required" : "Not permitted", detail: detail);

    private static IResult NoIdentity() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Caller not identified", detail: "The token has no object id (oid).");

    private static IResult NotFound(Guid id) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Claim not found", detail: $"No claim with id {id}.");

    private static IResult Conflict(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Decision not allowed", detail: detail);
}

/// <summary>Category for claim endpoint logs.</summary>
public sealed class ClaimsLog;
