using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ClaimsIntake.Api.Legacy;
using ClaimsIntake.Domain;
using ClaimsIntake.Domain.Security;
using ClaimsIntake.Legacy;
using ClaimsIntake.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ClaimsIntake.Api.Tests;

/// <summary>SEC-05: the legacy mock needs app-only tokens, with the role for what the caller does.</summary>
public class LegacyAuthenticationTests : IClassFixture<LegacyFactory>
{
    private readonly LegacyFactory _factory;

    public LegacyAuthenticationTests(LegacyFactory factory) => _factory = factory;

    private HttpClient As(string? token) => _factory.WithWebHostBuilder(_ => { }).CreateClient().WithBearer(token);

    private static HttpRequestMessage Post(string key = "k-1") =>
        new(HttpMethod.Post, "/claims")
        {
            Content = JsonContent.Create(new CreateClaimRequest("TM-CP-100001", "Brindle & Marsh Joinery Ltd", new DateOnly(2026, 11, 3), "Burst pipe.", 12_500m)),
            Headers = { { LegacyEndpoints.IdempotencyHeader, key } },
        };

    [Fact]
    public async Task An_anonymous_write_is_a_401_so_nobody_can_bypass_human_approval()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await As(null).SendAsync(Post())).StatusCode);
    }

    [Fact]
    public async Task A_token_without_claims_write_is_a_403_and_with_it_the_write_works()
    {
        var pipeline = LegacyFactory.App(AppRoles.ClaimsRead, AppRoles.PoliciesRead); // What the Function's identity has.
        var api = LegacyFactory.App(AppRoles.ClaimsWrite); // What the review API's identity has.

        Assert.Equal(HttpStatusCode.Forbidden, (await As(pipeline).SendAsync(Post("k-2"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await As(api).SendAsync(Post("k-3"))).StatusCode);
    }

    [Fact]
    public async Task A_user_token_with_the_write_role_is_still_refused_because_the_role_is_for_apps()
    {
        var user = TestAuth.Issue(TestAuth.LegacyClientId, [AppRoles.ClaimsWrite], name: "A Patel");

        Assert.Equal(HttpStatusCode.Forbidden, (await As(user).SendAsync(Post("k-4"))).StatusCode);
    }

    [Fact]
    public async Task Reads_need_their_own_roles()
    {
        var policiesOnly = LegacyFactory.App(AppRoles.PoliciesRead);
        var claimsOnly = LegacyFactory.App(AppRoles.ClaimsRead);
        var writerOnly = LegacyFactory.App(AppRoles.ClaimsWrite);

        Assert.Equal(HttpStatusCode.OK, (await As(policiesOnly).GetAsync("/policies/TM-CP-100001")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(claimsOnly).GetAsync("/policies/TM-CP-100001")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await As(claimsOnly).GetAsync("/claims?policyNumber=TM-CP-100001")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(policiesOnly).GetAsync("/claims?policyNumber=TM-CP-100001")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(writerOnly).GetAsync("/claims?policyNumber=TM-CP-100001")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(writerOnly).GetAsync("/claims/LC-000001")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await As(null).GetAsync("/policies/TM-CP-100001")).StatusCode);
    }

    [Fact]
    public async Task A_token_for_the_review_api_is_not_accepted_by_the_legacy_system()
    {
        var wrongAudience = TestAuth.Issue(TestAuth.ApiClientId, [AppRoles.ClaimsWrite], name: null, app: true);

        Assert.Equal(HttpStatusCode.Unauthorized, (await As(wrongAudience).SendAsync(Post("k-5"))).StatusCode);
    }

    [Fact]
    public async Task The_health_probe_is_anonymous()
    {
        Assert.Equal(HttpStatusCode.OK, (await As(null).GetAsync("/health/live")).StatusCode);
    }

    // ----- the dev personas against the legacy mock, through the real token handler and typed writer -----

    private sealed class DevLegacy : WebApplicationFactory<LegacyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Auth:Mode", "Dev");
        }
    }

    private sealed class Env(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    [Fact]
    public async Task The_review_apis_typed_writer_with_its_dev_token_can_write_and_the_pipelines_dev_token_cannot()
    {
        using var legacy = new DevLegacy();
        var claim = new Claim(Guid.NewGuid(), "inbox/a.pdf", ApiFactory.Now,
            [
                new ExtractedField(ClaimFields.PolicyNumber, "TM-CP-100001", 0.9),
                new ExtractedField(ClaimFields.ClaimantName, "Brindle & Marsh Joinery Ltd", 0.9),
                new ExtractedField(ClaimFields.LossDate, "2026-11-03", 0.9),
                new ExtractedField(ClaimFields.LossDescription, "Burst pipe.", 0.9),
            ], []);
        var apiHandler = new DevTokenHandler(DevPersonas.ReviewApi, new Env("Development")) { InnerHandler = legacy.Server.CreateHandler() };
        var pipelineHandler = new DevTokenHandler(DevPersonas.Pipeline, new Env("Development")) { InnerHandler = legacy.Server.CreateHandler() };
        var bare = legacy.CreateClient();

        var submission = await new LegacyClaimWriter(new HttpClient(apiHandler) { BaseAddress = legacy.Server.BaseAddress }).SubmitAsync(claim, default);
        var refused = await Assert.ThrowsAsync<LegacyRejectedException>(
            () => new LegacyClaimWriter(new HttpClient(pipelineHandler) { BaseAddress = legacy.Server.BaseAddress }).SubmitAsync(claim, default));
        var anonymous = await Assert.ThrowsAsync<LegacyRejectedException>(() => new LegacyClaimWriter(bare).SubmitAsync(claim, default));

        Assert.Equal("LC-000001", submission.Reference);
        Assert.Contains("403", refused.Message);
        Assert.Contains("401", anonymous.Message);
    }
}
