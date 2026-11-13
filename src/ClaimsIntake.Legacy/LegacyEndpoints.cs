namespace ClaimsIntake.Legacy;

public static class LegacyEndpoints
{
    public const string IdempotencyHeader = "Idempotency-Key";
    public const int MaxIdempotencyKeyLength = 128;
    private const int MaxTextLength = 4000;

    public static IEndpointRouteBuilder MapLegacyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/policies/{number}", GetPolicy);
        app.MapPost("/claims", CreateClaim);
        app.MapGet("/claims/{reference}", GetClaim);
        return app;
    }

    private static IResult GetPolicy(string number, IPolicyStore policies)
    {
        var policy = policies.Find(number);
        return policy is null
            ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Policy not found", detail: "No policy with that number.")
            : Results.Ok(policy);
    }

    private static IResult GetClaim(string reference, ILegacyClaimStore claims)
    {
        var claim = claims.Find(reference);
        return claim is null
            ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Claim not found", detail: "No claim with that reference.")
            : Results.Ok(claim);
    }

    private static IResult CreateClaim(
        HttpRequest http,
        CreateClaimRequest? request,
        IPolicyStore policies,
        ILegacyClaimStore claims)
    {
        var key = http.Headers[IdempotencyHeader].ToString().Trim();
        if (key.Length == 0)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Idempotency key required",
                detail: $"Send a unique {IdempotencyHeader} header so a retry cannot create a second claim.");
        }

        if (key.Length > MaxIdempotencyKeyLength)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Idempotency key too long",
                detail: $"The {IdempotencyHeader} header must be at most {MaxIdempotencyKeyLength} characters.");
        }

        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var policy = policies.Find(request!.PolicyNumber!);
        if (policy is null)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { ["policyNumber"] = ["No policy with that number."] },
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        return claims.Create(key, request, policy) switch
        {
            CreateOutcome.Created created =>
                Results.Created($"/claims/{created.Claim.Reference}", created.Claim),
            CreateOutcome.Replayed replayed =>
                new ReplayResult(replayed.Claim),
            _ => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Idempotency key already used",
                detail: $"This {IdempotencyHeader} was used with a different request. Use a new key for a new claim."),
        };
    }

    private static Dictionary<string, string[]> Validate(CreateClaimRequest? request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request is null)
        {
            errors["body"] = ["A JSON body is required."];
            return errors;
        }

        if (string.IsNullOrWhiteSpace(request.PolicyNumber))
        {
            errors["policyNumber"] = ["A policy number is required."];
        }

        if (string.IsNullOrWhiteSpace(request.ClaimantName))
        {
            errors["claimantName"] = ["A claimant name is required."];
        }

        if (request.LossDate is null)
        {
            errors["lossDate"] = ["A loss date is required (yyyy-MM-dd)."];
        }

        if (string.IsNullOrWhiteSpace(request.Description))
        {
            errors["description"] = ["A description is required."];
        }
        else if (request.Description.Length > MaxTextLength)
        {
            errors["description"] = [$"The description must be at most {MaxTextLength} characters."];
        }

        if (request.EstimatedAmountGbp is < 0)
        {
            errors["estimatedAmountGbp"] = ["The estimated amount cannot be negative."];
        }

        return errors;
    }

    /// <summary>200 OK with the original claim and an Idempotent-Replay header, for a repeated request.</summary>
    private sealed class ReplayResult(LegacyClaim claim) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers["Idempotent-Replay"] = "true";
            return Results.Ok(claim).ExecuteAsync(httpContext);
        }
    }
}
