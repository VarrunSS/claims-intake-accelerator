using System.Net;
using System.Text;
using ClaimsIntake.Pipeline.Legacy;

namespace ClaimsIntake.Pipeline.Tests;

public class ExistingClaimsTests
{
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Legacy_claims_for_a_policy_are_read_from_the_legacy_list_endpoint()
    {
        var handler = new StubHandler(_ => Json("""[{"reference":"LC-000001","policyNumber":"TM-CP-100001","claimantName":"Brindle & Marsh Joinery Ltd","lossDate":"2026-11-03","description":"x","estimatedAmountGbp":12500,"createdAt":"2026-11-13T10:00:00Z"}]"""));
        var source = new LegacyExistingClaims(new HttpClient(handler) { BaseAddress = new Uri("http://legacy.test/") });

        var claims = await source.FindByPolicyAsync("TM-CP-100001", CancellationToken.None);

        var claim = Assert.Single(claims);
        Assert.Equal(new ExistingClaim("LC-000001", "legacy", "TM-CP-100001", new DateOnly(2026, 11, 3), 12_500m, "Submitted"), claim);
        Assert.Equal("http://legacy.test/claims?policyNumber=TM-CP-100001", handler.Requests.Single().RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, handler.Requests.Single().Method);
    }

    [Fact]
    public async Task Review_store_claims_are_read_from_the_api_history_endpoint_which_has_no_names_or_text()
    {
        var id = Guid.NewGuid();
        var handler = new StubHandler(_ => Json($$"""[{"id":"{{id}}","policyNumber":"TM-CP-100001","lossDate":"2026-11-03","amountGbp":12500,"status":"NeedsReview"},{"id":"{{Guid.NewGuid()}}","policyNumber":"TM-CP-100001","lossDate":null,"amountGbp":null,"status":"Approved"}]"""));
        var source = new ReviewStoreExistingClaims(new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") });

        var claims = await source.FindByPolicyAsync("TM-CP-100001", CancellationToken.None);

        Assert.Equal(2, claims.Count);
        Assert.Equal(new ExistingClaim(id.ToString(), "review", "TM-CP-100001", new DateOnly(2026, 11, 3), 12_500m, "NeedsReview"), claims[0]);
        Assert.Null(claims[1].LossDate);
        Assert.Equal("http://api.test/claims/history?policyNumber=TM-CP-100001", handler.Requests.Single().RequestUri!.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "oops")]
    [InlineData(HttpStatusCode.OK, "not json")]
    public async Task A_source_that_fails_or_answers_badly_is_reported_as_unavailable(HttpStatusCode status, string body)
    {
        var legacy = new LegacyExistingClaims(new HttpClient(new StubHandler(_ => Json(body, status))) { BaseAddress = new Uri("http://legacy.test/") });
        var review = new ReviewStoreExistingClaims(new HttpClient(new StubHandler(_ => Json(body, status))) { BaseAddress = new Uri("http://api.test/") });

        await Assert.ThrowsAsync<LegacySystemUnavailableException>(() => legacy.FindByPolicyAsync("TM-CP-100001", CancellationToken.None));
        await Assert.ThrowsAsync<LegacySystemUnavailableException>(() => review.FindByPolicyAsync("TM-CP-100001", CancellationToken.None));
    }

    [Fact]
    public async Task The_composite_merges_its_sources_and_fails_if_any_cannot_be_reached()
    {
        var a = new FakeExistingClaims(new ExistingClaim("LC-1", "legacy", "TM-CP-100001", null, null, "Submitted"));
        var b = new FakeExistingClaims(new ExistingClaim("abc", "review", "TM-CP-100001", null, null, "NeedsReview"));

        var merged = await new CompositeExistingClaims([a, b]).FindByPolicyAsync("TM-CP-100001", CancellationToken.None);

        Assert.Equal(["LC-1", "abc"], merged.Select(c => c.Reference));
        var down = new LegacyExistingClaims(new HttpClient(new StubHandler(_ => throw new HttpRequestException("down"))) { BaseAddress = new Uri("http://legacy.test/") });
        await Assert.ThrowsAsync<LegacySystemUnavailableException>(() => new CompositeExistingClaims([a, down]).FindByPolicyAsync("TM-CP-100001", CancellationToken.None));
    }

    [Fact]
    public async Task The_policy_lookup_carries_the_wording_id_and_excess_from_the_legacy_system()
    {
        var handler = new StubHandler(_ => Json("""{"number":"TM-CP-100006","insuredName":"Wren & Co Estate Agents","product":"Office","periodStart":"2026-02-01","periodEnd":"2027-01-31","sumInsuredGbp":300000,"excessGbp":250,"wordingId":"TM-PW-OF-2026"}"""));
        var lookup = new LegacyPolicyLookup(new HttpClient(handler) { BaseAddress = new Uri("http://legacy.test/") });

        var policy = await lookup.FindAsync("TM-CP-100006", CancellationToken.None);

        Assert.Equal(("TM-PW-OF-2026", 250m), (policy!.WordingId, policy.ExcessGbp));
    }
}
