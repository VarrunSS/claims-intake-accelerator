using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaimsIntake.Api.Claims;
using ClaimsIntake.Api.Intake;
using ClaimsIntake.Domain;
using ClaimsIntake.Domain.Intake;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaimsIntake.Api.Tests;

public class IntakeTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static ProposedClaim Proposed(string documentId = "inbox/new.pdf", int pages = 1) => new(
        documentId,
        ApiFactory.Now,
        [
            new ExtractedField(ClaimFields.PolicyNumber, "TM-CP-100001", 0.97, new SourceSpan(documentId, 1, 15, 12)),
            new ExtractedField(ClaimFields.ClaimantName, "Brindle & Marsh Joinery Ltd", 0.93),
            new ExtractedField(ClaimFields.LossDate, "2026-11-03", 0.72),
            new ExtractedField(ClaimFields.LossDescription, null, 0.0),
        ],
        [new ValidationIssue(IssueCode.LowConfidence, IssueSeverity.Warning, ClaimFields.LossDate, "Low confidence.")],
        [.. Enumerable.Range(0, pages).Select(i => $"Page {i + 1}\nPolicy number: TM-CP-100001")]);

    private static ClaimIntakeService Service(InMemoryClaimRepository? repository = null) =>
        new(repository ?? new InMemoryClaimRepository(), new ClaimsIntake.Domain.Assessment.InMemoryAuditLog(), NullLogger<ClaimIntakeService>.Instance);

    // ----- The HTTP route (local mode) -----

    [Fact]
    public async Task A_proposed_claim_over_http_appears_in_the_review_queue_with_its_text_and_issues()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/intake/claims", Proposed(), ProposedClaim.JsonOptions);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<ClaimSummary>(Json);
        Assert.Equal(0.72, summary!.MinConfidence);
        Assert.Equal((0, 1), (summary.ErrorCount, summary.WarningCount));

        var detail = await client.GetFromJsonAsync<ClaimDetail>($"/claims/{summary.Id}", Json);
        Assert.Equal("inbox/new.pdf", detail!.SourceDocumentId);
        Assert.Equal(ClaimStatus.NeedsReview, detail.Status);
        Assert.Equal(["Page 1\nPolicy number: TM-CP-100001"], detail.SourcePages);
        Assert.Equal(4, detail.Fields.Count);
        Assert.Equal(15, detail.Fields.Single(f => f.Name == ClaimFields.PolicyNumber).Source!.Offset);
        Assert.Equal(IssueCode.LowConfidence, detail.Issues.Single().Code);

        var queue = await client.GetFromJsonAsync<List<ClaimSummary>>("/claims", Json);
        Assert.Contains(queue!, c => c.Id == summary.Id);
    }

    [Fact]
    public async Task Handing_over_the_same_document_twice_gives_one_claim()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var first = await client.PostAsJsonAsync("/intake/claims", Proposed(), ProposedClaim.JsonOptions);
        var second = await client.PostAsJsonAsync("/intake/claims", Proposed(), ProposedClaim.JsonOptions);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var ids = new[] { first, second }.Select(r => r.Content.ReadFromJsonAsync<ClaimSummary>(Json).GetAwaiter().GetResult()!.Id).Distinct();
        Assert.Single(ids);
        var all = await client.GetFromJsonAsync<List<ClaimSummary>>("/claims?status=all", Json);
        Assert.Single(all!, c => c.SourceDocumentId == "inbox/new.pdf");
    }

    [Fact]
    public async Task A_duplicate_does_not_reset_a_claim_that_has_already_been_decided()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var created = await (await client.PostAsJsonAsync("/intake/claims", Proposed(), ProposedClaim.JsonOptions))
            .Content.ReadFromJsonAsync<ClaimSummary>(Json);
        await client.PostAsJsonAsync($"/claims/{created!.Id}/reject", new RejectRequest("a.patel", "Not covered."));

        await client.PostAsJsonAsync("/intake/claims", Proposed(), ProposedClaim.JsonOptions);

        var detail = await client.GetFromJsonAsync<ClaimDetail>($"/claims/{created.Id}", Json);
        Assert.Equal(ClaimStatus.Rejected, detail!.Status);
    }

    [Theory]
    [InlineData("""{"documentId":"inbox/x.pdf","receivedAt":"2026-11-14T09:00:00Z","fields":[],"issues":[],"sourcePages":[]}""")]
    [InlineData("""{"documentId":"","receivedAt":"2026-11-14T09:00:00Z","fields":[{"name":"policy_number","value":"x","confidence":0.5}],"issues":[],"sourcePages":[]}""")]
    [InlineData("""{"documentId":"inbox/x.pdf","receivedAt":"2026-11-14T09:00:00Z","fields":[{"name":"policy_number","value":"x","confidence":7}],"issues":[],"sourcePages":[]}""")]
    [InlineData("""{"documentId":"inbox/x.pdf","receivedAt":"2026-11-14T09:00:00Z","fields":[{"name":"a","value":"x","confidence":0.5},{"name":"a","value":"y","confidence":0.5}],"issues":[],"sourcePages":[]}""")]
    [InlineData("""{"documentId":"inbox/x.pdf"}""")]
    [InlineData("not json")]
    [InlineData("null")]
    public async Task A_malformed_hand_over_is_refused_and_nothing_is_stored(string body)
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsync("/intake/claims", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var all = await client.GetFromJsonAsync<List<ClaimSummary>>("/claims?status=all", Json);
        Assert.DoesNotContain(all!, c => c.SourceDocumentId == "inbox/x.pdf");
    }

    [Fact]
    public async Task The_http_route_does_not_exist_unless_it_is_switched_on()
    {
        using var factory = new ApiFactory().WithWebHostBuilder(b => b.UseSetting("Intake:HttpEndpoint:Enabled", "false"));

        var response = await factory.CreateClient().PostAsJsonAsync("/intake/claims", Proposed(), ProposedClaim.JsonOptions);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ----- The service, which the Service Bus consumer also uses -----

    [Fact]
    public async Task A_queue_message_is_accepted_once_and_a_redelivery_is_a_duplicate()
    {
        var repository = new InMemoryClaimRepository();
        var service = Service(repository);
        var message = Proposed("inbox/queued.pdf").ToJson();

        var first = await service.AcceptJsonAsync(message, CancellationToken.None);
        var redelivered = await service.AcceptJsonAsync(message, CancellationToken.None);

        Assert.Equal(IntakeOutcome.Accepted, first.Outcome);
        Assert.Equal(IntakeOutcome.Duplicate, redelivered.Outcome);
        Assert.Equal(first.Claim!.Id, redelivered.Claim!.Id);
        Assert.Single(await repository.ListAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_deliveries_of_one_document_create_one_claim()
    {
        var repository = new InMemoryClaimRepository();
        var service = Service(repository);

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => service.AcceptAsync(Proposed("inbox/race.pdf"), CancellationToken.None)));

        Assert.Equal(1, results.Count(r => r.Outcome == IntakeOutcome.Accepted));
        Assert.Equal(9, results.Count(r => r.Outcome == IntakeOutcome.Duplicate));
        Assert.Single(await repository.ListAsync(null, CancellationToken.None));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"documentId":"d","receivedAt":"2026-11-14T09:00:00Z","fields":[{"name":"policy_number","value":"x","confidence":3}],"issues":[],"sourcePages":[]}""")]
    public async Task A_message_that_is_not_a_proposed_claim_is_invalid_so_it_can_be_dead_lettered(string body)
    {
        var repository = new InMemoryClaimRepository();

        var result = await Service(repository).AcceptJsonAsync(body, CancellationToken.None);

        Assert.Equal(IntakeOutcome.Invalid, result.Outcome);
        Assert.Empty(await repository.ListAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task Claims_beyond_the_size_limits_are_invalid()
    {
        var service = Service();
        var tooManyPages = Proposed(pages: ClaimIntakeService.MaxPages + 1);
        var hugeValue = Proposed() with
        {
            Fields = [new ExtractedField(ClaimFields.PolicyNumber, new string('x', ClaimIntakeService.MaxValueLength + 1), 0.9)],
        };
        var hugeText = Proposed() with { SourcePages = [new string('x', ClaimIntakeService.MaxTotalCharacters + 1)] };
        var longId = Proposed(new string('d', ClaimIntakeService.MaxDocumentIdLength + 1));

        foreach (var claim in new[] { tooManyPages, hugeValue, hugeText, longId })
        {
            Assert.Equal(IntakeOutcome.Invalid, (await service.AcceptAsync(claim, CancellationToken.None)).Outcome);
        }
    }

    [Fact]
    public async Task The_logs_for_an_accepted_claim_carry_ids_and_counts_but_not_claim_text()
    {
        var logger = new CapturingLogger();
        var service = new ClaimIntakeService(new InMemoryClaimRepository(), new ClaimsIntake.Domain.Assessment.InMemoryAuditLog(), logger);

        await service.AcceptAsync(Proposed(), CancellationToken.None);
        await service.AcceptJsonAsync("{ Brindle & Marsh not json", CancellationToken.None);

        var logged = string.Join('\n', logger.Lines);
        Assert.NotEmpty(logger.Lines);
        Assert.DoesNotContain("Brindle", logged);
        Assert.DoesNotContain("TM-CP-100001", logged);
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<ClaimIntakeService>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }
}

public class CorsTests
{
    private static async Task<HttpResponseMessage> Preflight(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<ApiMarker> factory, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/claims");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");
        return await factory.CreateClient().SendAsync(request);
    }

    [Fact]
    public async Task In_development_the_angular_dev_server_origin_is_allowed_and_no_other()
    {
        using var dev = new DevFactory();

        var allowed = await Preflight(dev, "http://localhost:4200");
        var other = await Preflight(dev, "https://evil.example");

        Assert.Equal("http://localhost:4200", Assert.Single(allowed.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.False(other.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Outside_development_no_origin_is_allowed()
    {
        using var factory = new ApiFactory();

        var response = await Preflight(factory, "http://localhost:4200");

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    /// <summary>The same test host as <see cref="ApiFactory"/>, but in the Development environment.</summary>
    private sealed class DevFactory : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<ApiMarker>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Telemetry:Exporter", "none");
        }
    }
}
