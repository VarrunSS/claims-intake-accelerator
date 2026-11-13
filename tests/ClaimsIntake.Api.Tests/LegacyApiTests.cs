using System.Net;
using System.Net.Http.Json;
using ClaimsIntake.Legacy;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ClaimsIntake.Api.Tests;

public class LegacyApiTests : IClassFixture<WebApplicationFactory<LegacyMarker>>
{
    private readonly WebApplicationFactory<LegacyMarker> _factory;

    public LegacyApiTests(WebApplicationFactory<LegacyMarker> factory) => _factory = factory;

    private static CreateClaimRequest Request(string policy = "TM-CP-100001", string description = "Burst pipe in the workshop.") =>
        new(policy, "Brindle & Marsh Joinery Ltd", new DateOnly(2026, 11, 3), description, 12_500m);

    private static HttpRequestMessage Post(CreateClaimRequest body, string? key)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/claims") { Content = JsonContent.Create(body) };
        if (key is not null)
        {
            message.Headers.Add(LegacyEndpoints.IdempotencyHeader, key);
        }

        return message;
    }

    // A new factory per test gives each test its own empty claim store.
    private HttpClient NewClient() => _factory.WithWebHostBuilder(_ => { }).CreateClient();

    [Fact]
    public async Task A_known_policy_is_returned()
    {
        var policy = await NewClient().GetFromJsonAsync<LegacyPolicy>("/policies/TM-CP-100001");

        Assert.Equal("Brindle & Marsh Joinery Ltd", policy!.InsuredName);
        Assert.Equal(new DateOnly(2026, 12, 31), policy.PeriodEnd);
    }

    [Theory]
    [InlineData("tm-cp-100002")]
    [InlineData("%20TM-CP-100002%20")]
    public async Task Policy_lookup_ignores_case_and_surrounding_spaces(string number)
    {
        var response = await NewClient().GetAsync($"/policies/{number}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_policy_is_a_404()
    {
        var response = await NewClient().GetAsync("/policies/TM-CP-000000");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void The_seed_data_has_ten_policies_with_distinct_numbers()
    {
        var store = new InMemoryPolicyStore();
        var found = Enumerable.Range(100001, 10).Select(n => store.Find($"TM-CP-{n}")).ToList();

        Assert.All(found, Assert.NotNull);
        Assert.Equal(10, found.Select(p => p!.Number).Distinct().Count());
        Assert.Null(store.Find("TM-CP-100011"));
    }

    [Fact]
    public async Task Creating_a_claim_without_an_idempotency_key_is_a_400()
    {
        var response = await NewClient().SendAsync(Post(Request(), key: null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Idempotency", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Creating_a_claim_with_a_blank_or_oversized_key_is_a_400()
    {
        var client = NewClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Post(Request(), key: " "))).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.SendAsync(Post(Request(), new string('k', LegacyEndpoints.MaxIdempotencyKeyLength + 1)))).StatusCode);
    }

    [Fact]
    public async Task A_new_claim_is_created_with_a_reference_and_can_be_fetched()
    {
        var client = NewClient();

        var response = await client.SendAsync(Post(Request(), "key-1"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var claim = await response.Content.ReadFromJsonAsync<LegacyClaim>();
        Assert.Equal("LC-000001", claim!.Reference);
        Assert.Equal("/claims/LC-000001", response.Headers.Location?.OriginalString);

        var fetched = await client.GetFromJsonAsync<LegacyClaim>(response.Headers.Location);
        Assert.Equal(claim, fetched);
    }

    [Fact]
    public async Task Retrying_with_the_same_key_and_body_returns_the_original_claim_without_creating_another()
    {
        var client = NewClient();
        var first = await client.SendAsync(Post(Request(), "retry-me"));
        var firstClaim = await first.Content.ReadFromJsonAsync<LegacyClaim>();

        var retry = await client.SendAsync(Post(Request(), "retry-me"));

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.True(retry.Headers.Contains("Idempotent-Replay"));
        Assert.Equal(firstClaim, await retry.Content.ReadFromJsonAsync<LegacyClaim>());

        // The next genuinely new claim gets the next reference, so the retry consumed none.
        var next = await client.SendAsync(Post(Request(), "another-key"));
        Assert.Equal("LC-000002", (await next.Content.ReadFromJsonAsync<LegacyClaim>())!.Reference);
    }

    [Fact]
    public async Task A_retry_that_differs_only_in_policy_number_case_and_spacing_is_the_same_request()
    {
        var client = NewClient();
        await client.SendAsync(Post(Request("TM-CP-100001"), "same"));

        var retry = await client.SendAsync(Post(Request(" tm-cp-100001 "), "same"));

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    [Fact]
    public async Task Reusing_a_key_with_a_different_body_is_a_409_and_creates_nothing()
    {
        var client = NewClient();
        await client.SendAsync(Post(Request(), "shared-key"));

        var clash = await client.SendAsync(Post(Request(description: "A different loss entirely."), "shared-key"));

        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        var next = await client.SendAsync(Post(Request(), "fresh-key"));
        Assert.Equal("LC-000002", (await next.Content.ReadFromJsonAsync<LegacyClaim>())!.Reference);
    }

    [Fact]
    public async Task Concurrent_requests_with_one_key_create_exactly_one_claim()
    {
        var client = NewClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => client.SendAsync(Post(Request(), "burst"))));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(9, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        var references = await Task.WhenAll(responses.Select(async r => (await r.Content.ReadFromJsonAsync<LegacyClaim>())!.Reference));
        Assert.Single(references.Distinct());
    }

    [Fact]
    public async Task A_claim_for_an_unknown_policy_is_a_422()
    {
        var response = await NewClient().SendAsync(Post(Request("TM-CP-000000"), "key-unknown"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task A_failed_request_does_not_consume_its_idempotency_key()
    {
        var client = NewClient();
        await client.SendAsync(Post(Request("TM-CP-000000"), "reuse-after-fail"));

        var fixedUp = await client.SendAsync(Post(Request("TM-CP-100001"), "reuse-after-fail"));

        Assert.Equal(HttpStatusCode.Created, fixedUp.StatusCode);
    }

    [Fact]
    public async Task A_claim_missing_required_fields_is_a_400_listing_each_one()
    {
        var response = await NewClient().SendAsync(Post(new CreateClaimRequest(null, null, null, null, -1m), "key-bad"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        foreach (var field in new[] { "policyNumber", "claimantName", "lossDate", "description", "estimatedAmountGbp" })
        {
            Assert.Contains(field, text);
        }
    }

    [Fact]
    public async Task An_unknown_claim_reference_is_a_404()
    {
        var response = await NewClient().GetAsync("/claims/LC-999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_legacy_system_reports_healthy()
    {
        var response = await NewClient().GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
