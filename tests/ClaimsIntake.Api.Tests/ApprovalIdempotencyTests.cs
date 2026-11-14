using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaimsIntake.Api.Claims;
using ClaimsIntake.Api.Legacy;
using ClaimsIntake.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace ClaimsIntake.Api.Tests;

/// <summary>
/// Approval writes to the legacy system. These tests show that however many times, and however, a handler
/// or a client repeats it, the legacy system ends up with one claim.
/// </summary>
public class ApprovalIdempotencyTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static Task<HttpResponseMessage> Approve(HttpClient client, Guid id, string reviewer = "a.patel") =>
        client.PostAsJsonAsync($"/claims/{id}/approve", new ApproveRequest(reviewer));

    private static async Task<ClaimDetail> Detail(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ClaimDetail>(Json))!;

    [Fact]
    public async Task Approving_writes_to_legacy_once_using_the_claim_id_as_the_idempotency_key()
    {
        using var factory = new ApiFactory();

        var response = await Approve(factory.CreateClient(), factory.Clean.Id);

        var claim = await Detail(response);
        Assert.Equal(ClaimStatus.Submitted, claim.Status);
        Assert.Equal("LC-000001", claim.LegacyReference);
        Assert.Equal([factory.Clean.Id], factory.Legacy.KeysSeen);
        Assert.Equal(1, factory.Legacy.Created);
    }

    [Fact]
    public async Task Approving_again_returns_the_same_reference_and_sends_nothing()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var first = await Detail(await Approve(client, factory.Clean.Id));

        var secondResponse = await Approve(client, factory.Clean.Id, reviewer: "someone.else");
        var second = await Detail(secondResponse);

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.True(secondResponse.Headers.Contains("Idempotent-Replay"));
        Assert.Equal(first.LegacyReference, second.LegacyReference);
        Assert.Equal("a.patel", second.DecidedBy); // The first decision stands.
        Assert.Equal(1, factory.Legacy.Calls);
        Assert.Equal(1, factory.Legacy.Created);
    }

    [Fact]
    public async Task When_the_legacy_system_is_down_the_claim_stays_approved_and_a_retry_completes_it()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        factory.Legacy.FailWith = new LegacyUnavailableException("down");

        var failed = await Approve(client, factory.Clean.Id);

        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.Contains("try again", await failed.Content.ReadAsStringAsync());
        Assert.Equal(ClaimStatus.Approved, factory.Clean.Status);
        Assert.Null(factory.Clean.LegacyReference);
        Assert.Equal(0, factory.Legacy.Created);

        factory.Legacy.FailWith = null;
        var retried = await Detail(await Approve(client, factory.Clean.Id));

        Assert.Equal(ClaimStatus.Submitted, retried.Status);
        Assert.Equal("LC-000001", retried.LegacyReference);
        Assert.Equal(2, factory.Legacy.Calls);
        Assert.Equal(1, factory.Legacy.Created);
        Assert.Single(factory.Legacy.KeysSeen.Distinct()); // Same key both times.
    }

    [Fact]
    public async Task A_lost_response_is_retried_with_the_same_key_and_creates_no_duplicate()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        factory.Legacy.LoseResponse = true; // The legacy system stores the claim, but we never hear back.

        var first = await Approve(client, factory.Clean.Id);

        Assert.Equal(HttpStatusCode.BadGateway, first.StatusCode);
        Assert.Equal(1, factory.Legacy.Created);

        factory.Legacy.LoseResponse = false;
        var retry = await Detail(await Approve(client, factory.Clean.Id));

        Assert.Equal("LC-000001", retry.LegacyReference);
        Assert.Equal(1, factory.Legacy.Created);
        Assert.Equal(2, factory.Legacy.Calls);
    }

    [Fact]
    public async Task A_refusal_by_the_legacy_system_is_reported_and_leaves_the_claim_approved()
    {
        using var factory = new ApiFactory();
        factory.Legacy.FailWith = new LegacyRejectedException("The legacy system refused the claim (HTTP 422).");

        var response = await Approve(factory.CreateClient(), factory.Clean.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ClaimStatus.Approved, factory.Clean.Status);
    }

    [Fact]
    public async Task A_claim_with_a_blocking_error_never_reaches_the_legacy_system()
    {
        using var factory = new ApiFactory();

        var response = await Approve(factory.CreateClient(), factory.WithError.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, factory.Legacy.Calls);
        Assert.Equal(ClaimStatus.NeedsReview, factory.WithError.Status);
    }

    [Fact]
    public async Task A_rejected_claim_never_reaches_the_legacy_system()
    {
        using var factory = new ApiFactory();

        var response = await Approve(factory.CreateClient(), factory.AlreadyRejected.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, factory.Legacy.Calls);
    }

    [Fact]
    public async Task A_claim_missing_something_legacy_requires_is_not_approved_at_all()
    {
        using var factory = new ApiFactory();
        var incomplete = new Claim(
            Guid.NewGuid(), "inbox/incomplete.pdf", ApiFactory.Now,
            [new ExtractedField(ClaimFields.PolicyNumber, "TM-CP-100001", 0.98), new ExtractedField(ClaimFields.ClaimantName, "Brindle & Marsh Joinery Ltd", 0.9),
             new ExtractedField(ClaimFields.LossDate, "2026-11-03", 0.9)],
            []);
        await factory.Services.GetRequiredService<IClaimRepository>().AddAsync(incomplete, CancellationToken.None);

        var response = await Approve(factory.CreateClient(), incomplete.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("description", await response.Content.ReadAsStringAsync());
        Assert.Equal(ClaimStatus.NeedsReview, incomplete.Status); // Still waiting for a handler to decide.
        Assert.Equal(0, factory.Legacy.Calls);
    }

    [Fact]
    public async Task Rejecting_a_submitted_claim_is_refused()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        await Approve(client, factory.Clean.Id);

        var response = await client.PostAsJsonAsync($"/claims/{factory.Clean.Id}/reject", new RejectRequest("b.jones", "Changed my mind."));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ClaimStatus.Submitted, factory.Clean.Status);
    }

    [Fact]
    public async Task Approving_an_unknown_claim_is_a_404_and_calls_nothing()
    {
        using var factory = new ApiFactory();

        var response = await Approve(factory.CreateClient(), Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, factory.Legacy.Calls);
    }

    [Fact]
    public async Task Concurrent_approvals_while_the_legacy_system_is_flaky_still_create_one_claim()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        factory.Legacy.LoseResponse = true;
        await Approve(client, factory.Clean.Id); // Stored in legacy, response lost.
        factory.Legacy.LoseResponse = false;

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Approve(client, factory.Clean.Id)));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, factory.Legacy.Created);
        Assert.Equal(2, factory.Legacy.Calls); // The lost one, then a single retry; the rest saw it was done.
    }
}
