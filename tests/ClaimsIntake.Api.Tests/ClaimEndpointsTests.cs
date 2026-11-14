using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaimsIntake.Api.Claims;
using ClaimsIntake.Domain;

namespace ClaimsIntake.Api.Tests;

public class ClaimEndpointsTests : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ApiFactory _factory;

    public ClaimEndpointsTests(ApiFactory factory) => _factory = factory;

    // Each test that changes state makes its own factory so tests cannot affect each other.
    private static ApiFactory Fresh() => new();

    [Fact]
    public async Task Listing_defaults_to_claims_waiting_for_review_oldest_first()
    {
        var client = _factory.CreateClient();

        var claims = await client.GetFromJsonAsync<List<ClaimSummary>>("/claims", Json);

        Assert.NotNull(claims);
        Assert.Equal(
            [_factory.Clean.Id, _factory.WithWarning.Id, _factory.WithError.Id],
            claims.Select(c => c.Id));
        Assert.All(claims, c => Assert.Equal(ClaimStatus.NeedsReview, c.Status));
    }

    [Fact]
    public async Task Listing_shows_issue_counts_so_handlers_can_triage()
    {
        var client = _factory.CreateClient();

        var claims = await client.GetFromJsonAsync<List<ClaimSummary>>("/claims", Json);

        var warning = Assert.Single(claims!, c => c.Id == _factory.WithWarning.Id);
        var error = Assert.Single(claims!, c => c.Id == _factory.WithError.Id);
        Assert.Equal((0, 1), (warning.ErrorCount, warning.WarningCount));
        Assert.Equal((1, 0), (error.ErrorCount, error.WarningCount));
    }

    [Theory]
    [InlineData("Rejected", 1)]
    [InlineData("rejected", 1)]
    [InlineData("all", 4)]
    [InlineData("Approved", 0)]
    public async Task Listing_can_be_filtered_by_status(string status, int expected)
    {
        var client = _factory.CreateClient();

        var claims = await client.GetFromJsonAsync<List<ClaimSummary>>($"/claims?status={status}", Json);

        Assert.Equal(expected, claims!.Count);
    }

    [Fact]
    public async Task An_unknown_status_filter_is_a_400_with_a_helpful_message()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/claims?status=Banana");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("NeedsReview", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Getting_a_claim_returns_its_fields_with_confidence_and_source()
    {
        var client = _factory.CreateClient();

        var claim = await client.GetFromJsonAsync<ClaimDetail>($"/claims/{_factory.Clean.Id}", Json);

        Assert.NotNull(claim);
        var policy = Assert.Single(claim.Fields, f => f.Name == ClaimFields.PolicyNumber);
        Assert.Equal("TM-CP-100001", policy.Value);
        Assert.Equal(0.98, policy.Confidence);
        Assert.Equal(new SourceSpan("inbox/clean.pdf", 1, 10, 12), policy.Source);
    }

    [Fact]
    public async Task Getting_an_unknown_claim_is_a_404_problem_response()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/claims/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_claim_id_that_is_not_a_guid_does_not_match_a_route()
    {
        var response = await _factory.CreateClient().GetAsync("/claims/not-a-guid");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Approving_a_claim_records_the_reviewer_and_time()
    {
        using var factory = Fresh();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/claims/{factory.Clean.Id}/approve", new ApproveRequest("a.patel"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var claim = await response.Content.ReadFromJsonAsync<ClaimDetail>(Json);
        Assert.Equal(ClaimStatus.Submitted, claim!.Status); // Approval also writes to the legacy system.
        Assert.Equal("LC-000001", claim.LegacyReference);
        Assert.Equal("a.patel", claim.DecidedBy);
        Assert.Equal(ApiFactory.Now, claim.DecidedAt);

        var queue = await client.GetFromJsonAsync<List<ClaimSummary>>("/claims", Json);
        Assert.DoesNotContain(queue!, c => c.Id == factory.Clean.Id);
    }

    [Fact]
    public async Task Approving_a_claim_with_a_data_quality_error_is_refused_with_a_409()
    {
        using var factory = Fresh();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/claims/{factory.WithError.Id}/approve", new ApproveRequest("a.patel"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ClaimStatus.NeedsReview, factory.WithError.Status);
    }

    [Fact]
    public async Task Rejecting_a_claim_stores_the_reason()
    {
        using var factory = Fresh();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/claims/{factory.WithError.Id}/reject", new RejectRequest("a.patel", "No policy number on the document."));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var claim = await response.Content.ReadFromJsonAsync<ClaimDetail>(Json);
        Assert.Equal(ClaimStatus.Rejected, claim!.Status);
        Assert.Equal("No policy number on the document.", claim.RejectionReason);
    }

    [Fact]
    public async Task Rejecting_without_a_reason_is_a_400_and_changes_nothing()
    {
        using var factory = Fresh();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/claims/{factory.Clean.Id}/reject", new RejectRequest("a.patel", " "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("reason", await response.Content.ReadAsStringAsync());
        Assert.Equal(ClaimStatus.NeedsReview, factory.Clean.Status);
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task A_decision_without_a_reviewer_is_a_400(string action)
    {
        using var factory = Fresh();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/claims/{factory.Clean.Id}/{action}", new RejectRequest(null, "Because."));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("reviewer", await response.Content.ReadAsStringAsync());
        Assert.Equal(ClaimStatus.NeedsReview, factory.Clean.Status);
    }

    [Fact]
    public async Task A_decision_with_no_body_is_a_400()
    {
        using var factory = Fresh();
        var client = factory.CreateClient();

        var response = await client.PostAsync($"/claims/{factory.Clean.Id}/approve", content: null);

        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType);
        Assert.Equal(ClaimStatus.NeedsReview, factory.Clean.Status);
    }

    [Fact]
    public async Task A_claim_cannot_be_decided_twice()
    {
        using var factory = Fresh();
        var client = factory.CreateClient();
        await client.PostAsJsonAsync($"/claims/{factory.Clean.Id}/approve", new ApproveRequest("a.patel"));

        var second = await client.PostAsJsonAsync(
            $"/claims/{factory.Clean.Id}/reject", new RejectRequest("b.jones", "Changed my mind."));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(ClaimStatus.Submitted, factory.Clean.Status);
        Assert.Equal("a.patel", factory.Clean.DecidedBy);
    }

    [Fact]
    public async Task A_claim_that_was_already_rejected_cannot_be_approved()
    {
        using var factory = Fresh();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/claims/{factory.AlreadyRejected.Id}/approve", new ApproveRequest("a.patel"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Deciding_an_unknown_claim_is_a_404()
    {
        using var factory = Fresh();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/claims/{Guid.NewGuid()}/approve", new ApproveRequest("a.patel"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Several_handlers_approving_at_once_write_to_the_legacy_system_once()
    {
        using var factory = Fresh();
        var client = factory.CreateClient();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            client.PostAsJsonAsync($"/claims/{factory.Clean.Id}/approve", new ApproveRequest($"handler{i}"))));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, factory.Legacy.Calls); // Calls for one claim are serialised, and later ones see it is done.
        Assert.Equal(1, results.Count(r => !r.Headers.Contains("Idempotent-Replay")));
        Assert.Equal(7, results.Count(r => r.Headers.Contains("Idempotent-Replay")));
        Assert.StartsWith("handler", factory.Clean.DecidedBy); // Whoever got in first is recorded; the others change nothing.
    }
}
