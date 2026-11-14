using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaimsIntake.Api.Claims;
using ClaimsIntake.Domain;
using ClaimsIntake.Domain.Assessment;
using ClaimsIntake.Domain.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ClaimsIntake.Api.Tests;

/// <summary>
/// SEC-01, SEC-02, SEC-03 and SEC-07 against the real authentication pipeline: Microsoft.Identity.Web JWT bearer, a fallback
/// policy, app roles, with tokens signed by a test key (see <see cref="TestAuth"/>).
/// </summary>
public class AuthenticationAndSegregationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static ApiFactory Routed() => new ApiFactory().WithRoutedClaims();

    private static HttpClient As(ApiFactory factory, string? token) => factory.CreateClient().WithBearer(token);

    // ----- 401: no token, a bad token -----

    [Fact]
    public async Task Every_claim_route_answers_401_without_a_token()
    {
        using var factory = Routed();
        var anonymous = As(factory, null);
        var id = factory.Clean.Id;

        var responses = new[]
        {
            await anonymous.GetAsync("/claims"),
            await anonymous.GetAsync($"/claims/{id}"),
            await anonymous.GetAsync($"/claims/{id}/audit"),
            await anonymous.GetAsync("/claims/history?policyNumber=TM-CP-100001"),
            await anonymous.GetAsync("/me"),
            await anonymous.PostAsync($"/claims/{id}/approve", null),
            await anonymous.PostAsJsonAsync($"/claims/{id}/reject", new RejectRequest("No.")),
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
        Assert.Equal(ClaimStatus.NeedsReview, factory.Clean.Status);
        Assert.Equal(0, factory.Legacy.Calls);
    }

    [Fact]
    public async Task The_intake_route_answers_401_without_a_token()
    {
        using var factory = new ApiFactory();
        var request = new HttpRequestMessage(HttpMethod.Post, "/intake/claims") { Content = JsonContent.Create(new { }) };

        var response = await As(factory, null).SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_health_probes_are_anonymous()
    {
        using var factory = new ApiFactory();

        var live = await As(factory, null).GetAsync("/health/live");
        var ready = await As(factory, null).GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task A_token_for_another_audience_is_not_accepted()
    {
        // A token issued for another API (here the legacy mock's) is not a valid credential for this one. Microsoft Identity Web
        // validates the audience, and a failed validation is a 401 (invalid_token), not a 403.
        using var factory = new ApiFactory();
        var token = TestAuth.Handler(audience: TestAuth.LegacyClientId);

        var response = await As(factory, token).GetAsync("/claims");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("invalid_token", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        using var factory = new ApiFactory();
        var token = TestAuth.Issue(TestAuth.ApiClientId, [AppRoles.ClaimsHandler], expires: DateTime.UtcNow.AddHours(-2));

        Assert.Equal(HttpStatusCode.Unauthorized, (await As(factory, token).GetAsync("/claims")).StatusCode);
    }

    [Fact]
    public async Task A_token_signed_with_another_key_is_refused()
    {
        using var factory = new ApiFactory();
        var otherKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray());
        var token = TestAuth.Issue(TestAuth.ApiClientId, [AppRoles.ClaimsHandler], key: otherKey);

        Assert.Equal(HttpStatusCode.Unauthorized, (await As(factory, token).GetAsync("/claims")).StatusCode);
    }

    // ----- 403: the wrong role -----

    [Fact]
    public async Task A_signed_in_person_with_the_wrong_app_role_gets_403_everywhere_in_the_claim_routes()
    {
        // (A token with no roles and no scopes at all is refused earlier, as a 401, by Microsoft.Identity.Web itself.)
        using var factory = Routed();
        var nobody = As(factory, TestAuth.Issue(TestAuth.ApiClientId, ["Auditor"]));

        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync("/claims")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync($"/claims/{factory.Clean.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.PostAsync($"/claims/{factory.Clean.Id}/approve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.PostAsJsonAsync($"/claims/{factory.Clean.Id}/reject", new RejectRequest("No."))).StatusCode);
        Assert.Equal(ClaimStatus.NeedsReview, factory.Clean.Status);
    }

    [Fact]
    public async Task An_app_only_token_cannot_act_as_a_handler_even_with_the_handler_role()
    {
        using var factory = Routed();
        var service = As(factory, TestAuth.Issue(TestAuth.ApiClientId, [AppRoles.ClaimsHandler, AppRoles.SeniorHandler], name: null, app: true));

        Assert.Equal(HttpStatusCode.Forbidden, (await service.GetAsync("/claims")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await service.PostAsync($"/claims/{factory.Clean.Id}/approve", null)).StatusCode);
    }

    [Fact]
    public async Task A_handler_can_read_the_queue_and_sees_who_they_are()
    {
        using var factory = new ApiFactory();

        var list = await As(factory, TestAuth.Handler()).GetAsync("/claims");
        var me = await As(factory, TestAuth.Senior()).GetFromJsonAsync<MeDto>("/me", Json);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(TestAuth.SeniorOid, me!.Id);
        Assert.Equal("B Jones", me.Name);
        Assert.Equal([AppRoles.SeniorHandler], me.Roles);
        Assert.True(me.IsSenior);
    }

    // ----- /claims/history: app-only (SEC-06) -----

    [Fact]
    public async Task History_accepts_the_pipelines_app_token_and_refuses_users_and_other_apps()
    {
        using var factory = new ApiFactory();
        const string url = "/claims/history?policyNumber=TM-CP-100001";

        var pipeline = await As(factory, TestAuth.Pipeline()).GetAsync(url);
        var handler = await As(factory, TestAuth.Handler()).GetAsync(url);
        // A USER token that carries the Intake.ReadHistory role is still refused: the endpoint is for apps only.
        var userWithRole = await As(factory, TestAuth.Issue(TestAuth.ApiClientId, [AppRoles.IntakeReadHistory])).GetAsync(url);
        var otherApp = await As(factory, TestAuth.Issue(TestAuth.ApiClientId, [AppRoles.ClaimsWrite], name: null, app: true)).GetAsync(url);
        var anonymous = await As(factory, null).GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, pipeline.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, handler.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, userWithRole.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, otherApp.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    // ----- SEC-03: segregation of duties, end to end -----

    [Fact]
    public async Task A_claims_handler_cannot_approve_a_claim_routed_to_senior_review()
    {
        using var factory = Routed();

        var response = await As(factory, TestAuth.Handler()).PostAsync($"/claims/{factory.SeniorRouted.Id}/approve", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Senior review required", problem.GetProperty("title").GetString());
        Assert.Equal(ClaimStatus.NeedsReview, factory.SeniorRouted.Status);
        Assert.Equal(0, factory.Legacy.Calls); // Nothing reached the legacy system.
        Assert.Empty(await factory.Audit.ForClaimAsync(factory.SeniorRouted.Id, default)); // And no decision was recorded.
    }

    [Fact]
    public async Task A_senior_handler_can_approve_a_senior_review_claim_and_the_decision_names_them()
    {
        using var factory = Routed();

        var response = await As(factory, TestAuth.Senior()).PostAsync($"/claims/{factory.SeniorRouted.Id}/approve", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<ClaimDetail>(Json);
        Assert.Equal(ClaimStatus.Submitted, detail!.Status);
        Assert.Equal(TestAuth.SeniorOid, detail.DecidedBy);
        Assert.Equal("B Jones", detail.DecidedByName);
        Assert.Equal(AppRoles.SeniorHandler, factory.SeniorRouted.DecidedAs);
    }

    [Fact]
    public async Task Both_roles_can_approve_a_claim_routed_to_a_handler_and_a_claim_with_no_assessment()
    {
        using var factory = Routed();

        Assert.Equal(HttpStatusCode.OK, (await As(factory, TestAuth.Handler()).PostAsync($"/claims/{factory.HandlerRouted.Id}/approve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await As(factory, TestAuth.Senior()).PostAsync($"/claims/{factory.Clean.Id}/approve", null)).StatusCode);
        Assert.Equal(AppRoles.ClaimsHandler, factory.HandlerRouted.DecidedAs);
    }

    [Fact]
    public async Task A_claims_handler_can_still_reject_a_claim_routed_to_senior_review()
    {
        using var factory = Routed();

        var response = await As(factory, TestAuth.Handler()).PostAsJsonAsync($"/claims/{factory.SeniorRouted.Id}/reject", new RejectRequest("Flood is excluded."));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ClaimStatus.Rejected, factory.SeniorRouted.Status);
    }

    [Fact]
    public async Task The_reviewer_is_the_token_whatever_the_body_says()
    {
        using var factory = Routed();

        // A body naming someone else is refused, so nobody can be framed.
        var framed = await As(factory, TestAuth.Handler()).PostAsJsonAsync($"/claims/{factory.Clean.Id}/approve", new { reviewer = "b.jones" });
        Assert.Equal(HttpStatusCode.BadRequest, framed.StatusCode);
        Assert.Null(factory.Clean.DecidedBy);

        var ok = await As(factory, TestAuth.Handler()).PostAsync($"/claims/{factory.Clean.Id}/approve", null);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(TestAuth.HandlerOid, factory.Clean.DecidedBy);
    }

    // ----- SEC-07: the local hand-off route -----

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task The_http_hand_off_route_does_not_exist_outside_development_even_if_enabled_by_a_stray_setting(string environment)
    {
        using var factory = new ApiFactory { Environment = environment };

        var request = new HttpRequestMessage(HttpMethod.Post, "/intake/claims") { Content = JsonContent.Create(new { }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.Pipeline());
        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task In_development_the_hand_off_route_needs_the_intake_submit_app_role()
    {
        using var factory = new ApiFactory();

        var asHandler = new HttpRequestMessage(HttpMethod.Post, "/intake/claims") { Content = JsonContent.Create(new { }) };
        asHandler.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.Handler());
        var readOnlyApp = new HttpRequestMessage(HttpMethod.Post, "/intake/claims") { Content = JsonContent.Create(new { }) };
        readOnlyApp.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            TestAuth.Issue(TestAuth.ApiClientId, [AppRoles.IntakeReadHistory], name: null, app: true));

        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CreateClient().SendAsync(asHandler)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CreateClient().SendAsync(readOnlyApp)).StatusCode);
    }

    // ----- fail closed at start-up -----

    [Fact]
    public void Without_entra_settings_the_service_does_not_start()
    {
        using var factory = new ApiFactory { Environment = "Production" }.WithWebHostBuilder(b =>
        {
            b.UseSetting("AzureAd:TenantId", "");
            b.UseSetting("AzureAd:ClientId", "");
        });

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("AzureAd:TenantId", ex.Message);
    }
}

/// <summary>
/// The dev authentication scheme (ADR 0009): works in Development, and cannot be switched on anywhere else.
/// </summary>
public class DevAuthenticationTests
{
    private sealed class DevFactory(string environment, params (string Key, string Value)[] settings) : WebApplicationFactory<ApiMarker>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("Telemetry:Exporter", "none");
            builder.UseSetting("Seed:DemoData", "false");
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        }
    }

    private static HttpClient Client(DevFactory factory, string? token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = token is null ? null : new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task In_development_the_dev_personas_work_and_an_unknown_token_does_not()
    {
        using var factory = new DevFactory("Development", ("Auth:Mode", "Dev"));

        Assert.Equal(HttpStatusCode.OK, (await Client(factory, "dev-handler").GetAsync("/claims")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client(factory, "dev-senior").GetAsync("/claims")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(factory, "dev-app-pipeline").GetAsync("/claims")).StatusCode); // An app, not a handler.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(factory, "dev-root").GetAsync("/claims")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(factory, null).GetAsync("/claims")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client(factory, null).GetAsync("/health/live")).StatusCode);
        var me = await Client(factory, "dev-senior").GetFromJsonAsync<MeDto>("/me", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(me!.IsSenior);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void Dev_authentication_cannot_be_switched_on_outside_the_development_environment(string environment)
    {
        using var factory = new DevFactory(environment, ("Auth:Mode", "Dev"));

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("only allowed in the Development environment", ex.Message);
    }

    [Theory]
    [InlineData("WEBSITE_SITE_NAME")]
    [InlineData("WEBSITE_INSTANCE_ID")]
    [InlineData("FUNCTIONS_WORKER_RUNTIME")]
    [InlineData("IDENTITY_ENDPOINT")]
    public void Dev_authentication_is_refused_on_an_azure_hosted_process_even_if_the_environment_says_development(string azureVariable)
    {
        using var factory = new DevFactory("Development", ("Auth:Mode", "Dev"), (azureVariable, "set-by-azure"));

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("Azure-hosted", ex.Message);
    }

    [Fact]
    public async Task A_dev_token_means_nothing_to_a_service_that_runs_entra()
    {
        using var factory = new ApiFactory { Environment = "Production" };

        var response = await factory.CreateClient().WithBearer("dev-senior").GetAsync("/claims");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_dev_token_means_nothing_to_entra_even_in_development()
    {
        using var factory = new ApiFactory { Environment = "Development" }; // Development, but Auth:Mode is Entra.

        var response = await factory.CreateClient().WithBearer("dev-senior").GetAsync("/claims");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void An_unknown_auth_mode_stops_the_app_at_start_up()
    {
        using var factory = new DevFactory("Development", ("Auth:Mode", "Anonymous"));

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("Entra", ex.Message);
    }

    [Fact]
    public void The_legacy_mock_refuses_dev_authentication_outside_development_too()
    {
        using var factory = new WebApplicationFactory<ClaimsIntake.Legacy.LegacyMarker>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Auth:Mode", "Dev");
        });

        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }
}
