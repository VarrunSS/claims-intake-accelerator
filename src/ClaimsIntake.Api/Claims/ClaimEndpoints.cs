using ClaimsIntake.Api.Telemetry;
using ClaimsIntake.Domain;

namespace ClaimsIntake.Api.Claims;

public static class ClaimEndpoints
{
    private const int MaxTextLength = 500;

    public static IEndpointRouteBuilder MapClaimEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/claims").WithTags("Claims");

        group.MapGet("/", ListClaims);
        group.MapGet("/{id:guid}", GetClaim);
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

    private static async Task<IResult> GetClaim(Guid id, IClaimRepository repository, CancellationToken ct)
    {
        var claim = await repository.GetAsync(id, ct);
        return claim is null ? NotFound(id) : Results.Ok(ClaimDetail.From(claim));
    }

    // Approving also writes to the legacy system, so it can be repeated: an approved claim whose write failed is
    // sent again, and one that was already written is returned as it is, with no second legacy claim.
    private static async Task<IResult> ApproveClaim(
        Guid id,
        ApproveRequest? request,
        ClaimApprovalService approvals,
        HttpResponse response,
        CancellationToken ct)
    {
        var errors = ValidateDecision(request?.Reviewer, reason: null, reasonRequired: false);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var result = await approvals.ApproveAsync(id, request!.Reviewer!, ct);
        switch (result.Outcome)
        {
            case ApprovalOutcome.Submitted:
                return Results.Ok(ClaimDetail.From(result.Claim!));
            case ApprovalOutcome.AlreadySubmitted:
                response.Headers["Idempotent-Replay"] = "true";
                return Results.Ok(ClaimDetail.From(result.Claim!));
            case ApprovalOutcome.NotFound:
                return NotFound(id);
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
        IClaimRepository repository,
        TimeProvider time,
        ILogger<ClaimsLog> logger,
        CancellationToken ct)
    {
        var errors = ValidateDecision(request?.Reviewer, request?.Reason, reasonRequired: true);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var claim = await repository.GetAsync(id, ct);
        if (claim is null)
        {
            return NotFound(id);
        }

        try
        {
            lock (claim)
            {
                claim.Reject(request!.Reviewer!, request.Reason!, time.GetUtcNow());
            }
        }
        catch (InvalidClaimTransitionException ex)
        {
            return Conflict(ex.Message);
        }

        await repository.SaveAsync(claim, ct);
        ApiMetrics.Decisions.Add(1, new KeyValuePair<string, object?>("decision", "rejected"));
        logger.LogInformation("Claim {ClaimId} rejected", claim.Id);
        return Results.Ok(ClaimDetail.From(claim));
    }

    // Reviewer identity comes from the request body until Entra ID sign-in is added (Milestone 6).
    private static Dictionary<string, string[]> ValidateDecision(string? reviewer, string? reason, bool reasonRequired)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(reviewer))
        {
            errors["reviewer"] = ["The reviewer must be identified."];
        }
        else if (reviewer.Length > MaxTextLength)
        {
            errors["reviewer"] = [$"The reviewer must be at most {MaxTextLength} characters."];
        }

        if (reasonRequired && string.IsNullOrWhiteSpace(reason))
        {
            errors["reason"] = ["A reason is required to reject a claim."];
        }
        else if (reason is { Length: > MaxTextLength })
        {
            errors["reason"] = [$"The reason must be at most {MaxTextLength} characters."];
        }

        return errors;
    }

    private static IResult NotFound(Guid id) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Claim not found", detail: $"No claim with id {id}.");

    private static IResult Conflict(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Decision not allowed", detail: detail);
}

/// <summary>Category for claim endpoint logs.</summary>
public sealed class ClaimsLog;
