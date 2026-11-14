using ClaimsIntake.Api.Claims;
using ClaimsIntake.Api.Http;
using Microsoft.AspNetCore.RateLimiting;
using ClaimsIntake.Security.AspNetCore;

namespace ClaimsIntake.Api.Intake;

public static class IntakeEndpoints
{
    public const string EnabledSetting = "Intake:HttpEndpoint:Enabled";

    /// <summary>The largest hand-off body accepted, in bytes. The only route that is allowed more than the 64 KB default (INP-03).</summary>
    public const long MaxBodyBytes = 8 * 1024 * 1024;

    /// <summary>
    /// POST /intake/claims: the local-mode hand-off from the inbox watcher (ADR 0005). It is off unless
    /// <c>Intake:HttpEndpoint:Enabled</c> is true AND the environment is Development (SEC-07): a stray app setting in Azure
    /// cannot open it. Even then it needs an app-only token with the Intake.Submit role.
    /// In Azure the pipeline hands claims over on a Service Bus queue instead.
    /// </summary>
    public static IEndpointRouteBuilder MapIntakeEndpoints(this IEndpointRouteBuilder app, IConfiguration configuration, IHostEnvironment environment)
    {
        if (configuration.GetValue<bool>(EnabledSetting) && environment.IsDevelopment())
        {
            app.MapPost("/intake/claims", Accept).RequireAuthorization(Policies.IntakeSubmit).WithTags("Intake")
                .WithBodyLimit(MaxBodyBytes).RequireRateLimiting(RateLimiting.Intake)
                .WithSummary("The local-mode hand-off from the inbox watcher (Development only). App-only token with Intake.Submit.")
                .Produces<ClaimSummary>(StatusCodes.Status201Created).Produces<ClaimSummary>().ProducesValidationProblem()
                .ProducesProblem(StatusCodes.Status413PayloadTooLarge).ProducesProblem(StatusCodes.Status429TooManyRequests);
        }

        return app;
    }

    // The body is read as text and parsed by the service, so that a body the domain types refuse (for example a
    // confidence outside 0 to 1) is a 400 like any other malformed body, not a server error.
    private static async Task<IResult> Accept(HttpRequest request, ClaimIntakeService intake, CancellationToken ct)
    {
        string body;
        try
        {
            using var reader = new StreamReader(request.Body);
            body = await reader.ReadToEndAsync(ct);
        }
        catch (BadHttpRequestException)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["body"] = ["The body is too large."] });
        }

        var result = await intake.AcceptJsonAsync(body, ct);
        return result.Outcome switch
        {
            IntakeOutcome.Accepted => Results.Created($"/claims/{result.Claim!.Id}", ClaimSummary.From(result.Claim)),
            IntakeOutcome.Duplicate => Results.Ok(ClaimSummary.From(result.Claim!)),
            _ => Results.ValidationProblem(new Dictionary<string, string[]> { ["body"] = [.. result.Problems!] }),
        };
    }
}
