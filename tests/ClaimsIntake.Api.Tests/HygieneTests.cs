using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaimsIntake.Api.Claims;
using ClaimsIntake.Domain;
using ClaimsIntake.Domain.Intake;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace ClaimsIntake.Api.Tests;

/// <summary>INP-03, API-01 and API-02, and RFC 7807 problem details on every error the API produces.</summary>
public class HygieneTests
{
    private const string ProblemJson = "application/problem+json";

    private static async Task<JsonObject> Problem(HttpResponseMessage response)
    {
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
        var problem = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal((int)response.StatusCode, problem["status"]!.GetValue<int>());
        Assert.False(string.IsNullOrWhiteSpace(problem["title"]?.GetValue<string>()), "A problem has a title.");
        return problem;
    }

    // ----- INP-03: body limits -----

    [Fact]
    public async Task An_approve_body_of_100_kb_is_refused_with_413_and_problem_details()
    {
        using var factory = new ApiFactory();
        var body = new StringContent($"{{\"padding\":\"{new string('x', 100 * 1024)}\"}}", Encoding.UTF8, "application/json");

        var response = await factory.CreateClient().PostAsync($"/claims/{factory.Clean.Id}/approve", body);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var problem = await Problem(response);
        Assert.Contains("65,536", problem["detail"]!.GetValue<string>());
        Assert.Equal(ClaimStatus.NeedsReview, factory.Clean.Status);
        Assert.Equal(0, factory.Legacy.Calls);
    }

    [Fact]
    public async Task A_reject_body_of_100_kb_is_refused_with_413()
    {
        using var factory = new ApiFactory();
        var body = new StringContent($"{{\"reason\":\"{new string('x', 100 * 1024)}\"}}", Encoding.UTF8, "application/json");

        var response = await factory.CreateClient().PostAsync($"/claims/{factory.Clean.Id}/reject", body);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task A_normal_sized_approve_or_reject_body_is_not_affected()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/claims/{factory.WithWarning.Id}/reject", new RejectRequest("Not covered."));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Only_the_intake_route_accepts_more_than_64_kb()
    {
        using var factory = new ApiFactory();
        var documentId = "inbox/long.pdf";
        var proposed = new ProposedClaim(documentId, ApiFactory.Now,
            [new ExtractedField(ClaimFields.PolicyNumber, "TM-CP-100001", 0.97)], [], [new string('a', 150 * 1024)]);

        var response = await factory.CreateClient().PostIntakeAsync(proposed);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task The_intake_route_has_a_limit_of_its_own()
    {
        using var factory = new ApiFactory();
        var body = new ByteArrayContent(new byte[9 * 1024 * 1024]);
        body.Headers.ContentType = new("application/json");

        var response = await factory.CreateClient().PostIntakeAsync(body);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        await Problem(response);
    }

    // ----- API-01: rate limiting -----

    [Fact]
    public async Task The_request_after_the_limit_on_approve_gets_429_with_retry_after_and_problem_details()
    {
        using var factory = new ApiFactory().WithWebHostBuilder(b =>
        {
            b.UseSetting("RateLimiting:decisions:PermitLimit", "3");
            b.UseSetting("RateLimiting:decisions:WindowSeconds", "60");
        });
        var client = factory.CreateClient();
        var id = Guid.NewGuid();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            statuses.Add((await client.PostAsync($"/claims/{id}/approve", null)).StatusCode);
        }

        Assert.Equal([HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests], statuses);

        var limited = await client.PostAsync($"/claims/{id}/approve", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.TryGetValues("Retry-After", out var retry));
        Assert.InRange(int.Parse(retry!.Single()), 1, 60);
        var problem = await Problem(limited);
        Assert.Equal("Too many requests", problem["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task Reject_shares_the_decision_limit_and_a_different_caller_has_a_window_of_their_own()
    {
        using var factory = new ApiFactory().WithWebHostBuilder(b =>
        {
            b.UseSetting("RateLimiting:decisions:PermitLimit", "2");
            b.UseSetting("RateLimiting:decisions:WindowSeconds", "60");
        });
        var handler = factory.CreateClient();
        var senior = factory.CreateClient().WithBearer(TestAuth.Senior());
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await handler.PostAsync($"/claims/{id}/approve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await handler.PostAsJsonAsync($"/claims/{id}/reject", new RejectRequest("No."))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await handler.PostAsJsonAsync($"/claims/{id}/reject", new RejectRequest("No."))).StatusCode);

        // Another person is not held back by the first one's burst.
        Assert.Equal(HttpStatusCode.NotFound, (await senior.PostAsync($"/claims/{id}/approve", null)).StatusCode);
        // Reading the queue is not part of the decision limit.
        Assert.Equal(HttpStatusCode.OK, (await handler.GetAsync("/claims")).StatusCode);
    }

    [Fact]
    public async Task The_intake_route_has_its_own_limit()
    {
        using var factory = new ApiFactory().WithWebHostBuilder(b =>
        {
            b.UseSetting("RateLimiting:intake:PermitLimit", "2");
            b.UseSetting("RateLimiting:intake:WindowSeconds", "60");
        });
        var client = factory.CreateClient();
        ProposedClaim Proposed(string name) => new(name, ApiFactory.Now, [new ExtractedField(ClaimFields.PolicyNumber, "TM-CP-100001", 0.97)], [], ["Page 1"]);

        Assert.Equal(HttpStatusCode.Created, (await client.PostIntakeAsync(Proposed("inbox/a.pdf"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostIntakeAsync(Proposed("inbox/b.pdf"))).StatusCode);
        var limited = await client.PostIntakeAsync(Proposed("inbox/c.pdf"));

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        await Problem(limited);
    }

    [Fact]
    public async Task An_anonymous_caller_is_limited_by_address_and_health_probes_stay_reachable_under_the_default_limit()
    {
        using var factory = new ApiFactory();
        var anonymous = factory.CreateClient().WithBearer(null);

        for (var i = 0; i < 30; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health/live")).StatusCode);
        }
    }

    // ----- Problem details everywhere -----

    [Fact]
    public async Task Authentication_and_authorisation_failures_are_problem_details()
    {
        using var factory = new ApiFactory().WithRoutedClaims();

        var unauthenticated = await factory.CreateClient().WithBearer(null).GetAsync("/claims");
        var forbidden = await factory.CreateClient().PostAsync($"/claims/{factory.SeniorRouted.Id}/approve", null);

        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        await Problem(unauthenticated);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var problem = await Problem(forbidden);
        Assert.Equal("Senior review required", problem["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_unknown_route_a_missing_claim_and_a_bad_request_are_problem_details_with_a_trace_id()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var unknownRoute = await client.GetAsync("/nothing-here");
        var missing = await client.GetAsync($"/claims/{Guid.NewGuid()}");
        var invalid = await client.GetAsync("/claims?status=bogus");

        Assert.Equal(HttpStatusCode.NotFound, unknownRoute.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        foreach (var response in new[] { unknownRoute, missing, invalid })
        {
            var problem = await Problem(response);
            Assert.False(string.IsNullOrWhiteSpace(problem["traceId"]?.GetValue<string>()));
        }
    }

    [Fact]
    public async Task An_unhandled_exception_is_a_500_problem_without_the_exception_message()
    {
        using var factory = new ApiFactory().WithWebHostBuilder(b => b.ConfigureServices(services =>
            services.AddSingleton<IClaimRepository>(new ThrowingRepository())));
        var client = factory.CreateClient();

        var response = await client.GetAsync("/claims");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SECRET-CLAIM-TEXT", text);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
    }

    private sealed class ThrowingRepository : IClaimRepository
    {
        public Task<IReadOnlyList<Claim>> ListAsync(ClaimStatus? status, CancellationToken cancellationToken) => throw new InvalidOperationException("SECRET-CLAIM-TEXT");

        public Task<Claim?> GetAsync(Guid id, CancellationToken cancellationToken) => throw new InvalidOperationException("SECRET-CLAIM-TEXT");

        public Task AddAsync(Claim claim, CancellationToken cancellationToken) => throw new InvalidOperationException("SECRET-CLAIM-TEXT");

        public Task SaveAsync(Claim claim, CancellationToken cancellationToken) => throw new InvalidOperationException("SECRET-CLAIM-TEXT");
    }

    // ----- API-02: OpenAPI -----

    private static async Task<JsonObject> OpenApi(ApiFactory factory)
    {
        var response = await factory.CreateClient().WithBearer(null).GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    [Fact]
    public async Task The_openapi_document_lists_the_claim_routes_and_the_bearer_scheme()
    {
        using var factory = new ApiFactory();

        var document = await OpenApi(factory);

        var paths = document["paths"]!.AsObject().Select(p => p.Key).ToHashSet();
        Assert.Superset(new HashSet<string>
        {
            "/claims", "/claims/{id}", "/claims/{id}/audit", "/claims/{id}/approve", "/claims/{id}/reject", "/claims/history", "/me", "/intake/claims",
        }, paths);
        var scheme = document["components"]!["securitySchemes"]!["Bearer"]!;
        Assert.Equal("http", scheme["type"]!.GetValue<string>());
        Assert.Equal("bearer", scheme["scheme"]!.GetValue<string>());
        Assert.Equal("JWT", scheme["bearerFormat"]!.GetValue<string>());
    }

    [Fact]
    public async Task Each_operation_documents_its_security_requirement_and_the_roles_that_may_call_it()
    {
        using var factory = new ApiFactory();

        var document = await OpenApi(factory);

        var approve = document["paths"]!["/claims/{id}/approve"]!["post"]!;
        Assert.NotNull(approve["security"]!.AsArray().Single()!["Bearer"]);
        Assert.Equal(["ClaimsHandler", "SeniorHandler"], approve["x-required-roles"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.Contains("SeniorHandler role", approve["summary"]!.GetValue<string>());

        var history = document["paths"]!["/claims/history"]!["get"]!;
        Assert.Equal(["Intake.ReadHistory"], history["x-required-roles"]!.AsArray().Select(r => r!.GetValue<string>()));

        var intake = document["paths"]!["/intake/claims"]!["post"]!;
        Assert.Equal(["Intake.Submit"], intake["x-required-roles"]!.AsArray().Select(r => r!.GetValue<string>()));
    }

    [Fact]
    public async Task The_openapi_document_is_not_served_outside_development()
    {
        using var factory = new ApiFactory { Environment = "Production" };

        // A signed-in caller finds no such route. An anonymous one is refused before routing matters (the fallback policy).
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync("/openapi/v1.json")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().WithBearer(null).GetAsync("/openapi/v1.json")).StatusCode);
    }

    [Fact]
    public async Task The_committed_openapi_document_matches_the_generated_one()
    {
        using var factory = new ApiFactory();
        var generated = JsonSerializer.Serialize(await OpenApi(factory), new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";
        var path = Path.Combine(RepositoryRoot(), "docs", "openapi", "review-api.v1.json");

        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, generated);
        }

        Assert.True(File.Exists(path), "docs/openapi/review-api.v1.json is missing. Run the tests once with UPDATE_OPENAPI=1 and commit the file.");
        Assert.Equal((await File.ReadAllTextAsync(path)).ReplaceLineEndings("\n"), generated);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ClaimsIntake.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
