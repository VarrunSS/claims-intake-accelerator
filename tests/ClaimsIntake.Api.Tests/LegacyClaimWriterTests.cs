using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClaimsIntake.Api.Claims;
using ClaimsIntake.Api.Legacy;
using ClaimsIntake.Domain;
using ClaimsIntake.Legacy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaimsIntake.Api.Tests;

public class LegacyClaimWriterTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }

    private static Claim NewClaim(string amount = "£12,500", string lossDate = "2026-11-03") => new(
        Guid.NewGuid(), "inbox/a.pdf", ApiFactory.Now,
        [
            new ExtractedField(ClaimFields.PolicyNumber, "TM-CP-100001", 0.98),
            new ExtractedField(ClaimFields.ClaimantName, "Brindle & Marsh Joinery Ltd", 0.9),
            new ExtractedField(ClaimFields.LossDate, lossDate, 0.93),
            new ExtractedField(ClaimFields.LossDescription, "Burst pipe.", 0.9),
            new ExtractedField(ClaimFields.EstimatedAmount, amount, 0.9),
        ],
        []);

    private static LegacyClaimWriter Writer(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://legacy.test/") });

    private static HttpResponseMessage Created(string reference = "LC-000007") => new(HttpStatusCode.Created)
    {
        Content = new StringContent($$"""{"reference":"{{reference}}"}""", Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task The_claim_id_is_sent_as_the_idempotency_key_with_the_mapped_body()
    {
        var handler = new StubHandler(_ => Created());
        var claim = NewClaim();

        var result = await Writer(handler).SubmitAsync(claim, CancellationToken.None);

        Assert.Equal(new LegacySubmission("LC-000007", false), result);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://legacy.test/claims", request.RequestUri!.ToString());
        Assert.Equal(claim.Id.ToString("D"), Assert.Single(request.Headers.GetValues("Idempotency-Key")));

        using var json = JsonDocument.Parse(body);
        Assert.Equal("TM-CP-100001", json.RootElement.GetProperty("policyNumber").GetString());
        Assert.Equal("2026-11-03", json.RootElement.GetProperty("lossDate").GetString());
        Assert.Equal(12500m, json.RootElement.GetProperty("estimatedAmountGbp").GetDecimal());
    }

    [Fact]
    public async Task The_same_claim_always_produces_the_same_key_and_the_same_body()
    {
        var handler = new StubHandler(_ => Created());
        var writer = Writer(handler);
        var claim = NewClaim();

        await writer.SubmitAsync(claim, CancellationToken.None);
        await writer.SubmitAsync(claim, CancellationToken.None);

        Assert.Equal(handler.Requests[0].Request.Headers.GetValues("Idempotency-Key"), handler.Requests[1].Request.Headers.GetValues("Idempotency-Key"));
        Assert.Equal(handler.Requests[0].Body, handler.Requests[1].Body);
    }

    [Fact]
    public async Task A_replay_answer_is_recognised()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"reference":"LC-000003"}""", Encoding.UTF8, "application/json"),
            };
            response.Headers.Add("Idempotent-Replay", "true");
            return response;
        });

        var result = await Writer(handler).SubmitAsync(NewClaim(), CancellationToken.None);

        Assert.Equal(new LegacySubmission("LC-000003", true), result);
    }

    [Theory]
    [InlineData("£3,200.50", 3200.50)]
    [InlineData("3200", 3200)]
    public void Amounts_are_read_with_or_without_the_pound_sign_and_commas(string text, double expected)
    {
        Assert.True(LegacyClaimMapper.TryMap(NewClaim(amount: text), out var request, out _));
        Assert.Equal((decimal)expected, request.EstimatedAmountGbp);
    }

    [Fact]
    public void An_unreadable_amount_is_left_out_rather_than_guessed()
    {
        Assert.True(LegacyClaimMapper.TryMap(NewClaim(amount: "3,2OO"), out var request, out _));
        Assert.Null(request.EstimatedAmountGbp);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task A_client_error_means_the_claim_was_refused(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status) { Content = new StringContent("echo of Brindle & Marsh") });

        var ex = await Assert.ThrowsAsync<LegacyRejectedException>(() => Writer(handler).SubmitAsync(NewClaim(), CancellationToken.None));

        Assert.Contains(((int)status).ToString(), ex.Message);
        Assert.DoesNotContain("Brindle", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_server_error_timeout_or_throttle_is_worth_retrying(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status));

        await Assert.ThrowsAsync<LegacyUnavailableException>(() => Writer(handler).SubmitAsync(NewClaim(), CancellationToken.None));
    }

    [Fact]
    public async Task A_network_failure_is_worth_retrying()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("refused"));

        await Assert.ThrowsAsync<LegacyUnavailableException>(() => Writer(handler).SubmitAsync(NewClaim(), CancellationToken.None));
    }

    [Fact]
    public async Task A_success_without_a_reference_is_not_trusted()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });

        await Assert.ThrowsAsync<LegacyUnavailableException>(() => Writer(handler).SubmitAsync(NewClaim(), CancellationToken.None));
    }

    [Fact]
    public async Task A_claim_that_cannot_be_mapped_is_not_sent()
    {
        var handler = new StubHandler(_ => Created());

        await Assert.ThrowsAsync<LegacyRejectedException>(() => Writer(handler).SubmitAsync(NewClaim(lossDate: "last Tuesday"), CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    // ----- Against the real legacy mock, in memory -----

    [Fact]
    public async Task Against_the_real_legacy_mock_repeated_approvals_make_one_claim_and_use_up_one_number()
    {
        using var legacy = new WebApplicationFactory<LegacyMarker>();
        using var factory = new ApiFactory();
        using var api = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ILegacyClaimWriter>();
            services.AddSingleton<ILegacyClaimWriter>(new LegacyClaimWriter(legacy.CreateClient()));
        }));
        var client = api.CreateClient();
        var second = await Seed(api, "inbox/second.pdf");
        var third = await Seed(api, "inbox/third.pdf");

        var firstTry = await client.PostAsJsonAsync($"/claims/{factory.Clean.Id}/approve", new ApproveRequest("a.patel"));
        var firstRetry = await client.PostAsJsonAsync($"/claims/{factory.Clean.Id}/approve", new ApproveRequest("a.patel"));
        var other = await client.PostAsJsonAsync($"/claims/{second.Id}/approve", new ApproveRequest("a.patel"));
        var firstAgain = await client.PostAsJsonAsync($"/claims/{factory.Clean.Id}/approve", new ApproveRequest("a.patel"));
        var next = await client.PostAsJsonAsync($"/claims/{third.Id}/approve", new ApproveRequest("a.patel"));

        string? Reference(HttpResponseMessage r) => r.Content.ReadFromJsonAsync<JsonElement>().GetAwaiter().GetResult().GetProperty("legacyReference").GetString();
        Assert.Equal("LC-000001", Reference(firstTry));
        Assert.Equal("LC-000001", Reference(firstRetry));
        Assert.Equal("LC-000002", Reference(other));
        Assert.Equal("LC-000001", Reference(firstAgain));
        Assert.Equal("LC-000003", Reference(next)); // Replays did not consume numbers.

        var stored = await legacy.CreateClient().GetFromJsonAsync<JsonElement>("/claims/LC-000001");
        Assert.Equal("TM-CP-100001", stored.GetProperty("policyNumber").GetString());
        Assert.Equal("2026-11-03", stored.GetProperty("lossDate").GetString());
        Assert.Equal(12500m, stored.GetProperty("estimatedAmountGbp").GetDecimal());
    }

    private static async Task<Claim> Seed(WebApplicationFactory<ApiMarker> factory, string document)
    {
        var claim = new Claim(
            Guid.NewGuid(), document, ApiFactory.Now,
            [
                new ExtractedField(ClaimFields.PolicyNumber, "TM-CP-100002", 0.98),
                new ExtractedField(ClaimFields.ClaimantName, "Kestrel Print & Packaging Ltd", 0.9),
                new ExtractedField(ClaimFields.LossDate, "2026-10-21", 0.93),
                new ExtractedField(ClaimFields.LossDescription, "Theft.", 0.9),
            ],
            []);
        await factory.Services.GetRequiredService<IClaimRepository>().AddAsync(claim, CancellationToken.None);
        return claim;
    }
}
