using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Data.Tables;
using ClaimsIntake.Api.Audit;
using ClaimsIntake.Api.Claims;
using ClaimsIntake.Api.Intake;
using ClaimsIntake.Domain;
using ClaimsIntake.Domain.Assessment;
using ClaimsIntake.Domain.Intake;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaimsIntake.Api.Tests;

public class AuditAndAssessmentTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly Guid DocumentClaimId = ClaimIds.ForDocument("inbox/assessed.pdf");

    private static ClauseCitation Flood => new("TM-PW-OF-2026", "4.1", "Flood", "We will not pay for loss or damage caused by flood.");

    private static ProposedClaim Assessed(string documentId = "inbox/assessed.pdf", int auditEntries = 3)
    {
        var claimId = ClaimIds.ForDocument(documentId);
        var start = ApiFactory.Now;
        var tools = new[] { "lookup_policy", "search_policy_wording", "check_duplicate", "propose_claim" };
        return new ProposedClaim(
            documentId, ApiFactory.Now,
            [
                new ExtractedField(ClaimFields.PolicyNumber, "TM-CP-100009", 0.97),
                new ExtractedField(ClaimFields.ClaimantName, "Greyfriars Bookbinders", 0.95),
                new ExtractedField(ClaimFields.LossDate, "2026-11-10", 0.95),
                new ExtractedField(ClaimFields.LossDescription, "Surface water flooded the workshop.", 0.9),
                new ExtractedField(ClaimFields.EstimatedAmount, "£7,800", 0.9),
            ],
            [],
            ["Policy number: TM-CP-100009"],
            new ClaimAssessment("rules",
                [new CoverageFinding(FindingKind.PossibleExclusion, "Flood may be excluded.", [Flood])],
                ReviewRoute.SeniorReview, "Needs senior review: a possible exclusion.", []),
            [.. Enumerable.Range(0, auditEntries).Select(i => new AuditEntry(
                Guid.Empty, tools[i % tools.Length], new Dictionary<string, string> { ["policy_number"] = "TM-CP-100009" },
                $"result {i}", 1.5 + i, start.AddMilliseconds(10 * (auditEntries - i))))]);
    }

    private static async Task<ClaimSummary> Post(HttpClient client, ProposedClaim claim)
    {
        var response = await client.PostAsJsonAsync("/intake/claims", claim, ProposedClaim.JsonOptions);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ClaimSummary>(Json))!;
    }

    // ----- GET /claims/{id}/audit -----

    [Fact]
    public async Task The_audit_endpoint_returns_the_tool_calls_stored_at_intake_oldest_first()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var created = await Post(client, Assessed());

        var response = await client.GetAsync($"/claims/{created.Id}/audit");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entries = (await response.Content.ReadFromJsonAsync<List<AuditEntryDto>>(Json))!;
        Assert.Equal(3, entries.Count);
        // The trail was sent newest first; the endpoint sorts by time.
        Assert.Equal(["check_duplicate", "search_policy_wording", "lookup_policy"], entries.Select(e => e.Tool));
        Assert.Equal("TM-CP-100009", entries[0].Arguments["policy_number"]);
        Assert.All(entries, e => Assert.True(e.Succeeded));
        Assert.True(entries.All(e => e.DurationMs > 0));
    }

    [Fact]
    public async Task The_trail_is_stored_under_the_claim_id_the_api_derived_whatever_id_the_message_carried()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var proposed = Assessed();

        var created = await Post(client, proposed with { AuditTrail = [.. proposed.AuditTrail!.Select(a => a with { ClaimId = Guid.NewGuid() })] });

        Assert.Equal(DocumentClaimId, created.Id);
        var stored = await factory.Services.GetRequiredService<IAuditLog>().ForClaimAsync(created.Id, CancellationToken.None);
        Assert.Equal(3, stored.Count);
        Assert.All(stored, e => Assert.Equal(created.Id, e.ClaimId));
    }

    [Fact]
    public async Task A_claim_with_no_tool_calls_has_an_empty_audit_trail_and_an_unknown_claim_is_a_404()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var empty = await client.GetFromJsonAsync<List<AuditEntryDto>>($"/claims/{factory.Clean.Id}/audit", Json);
        var missing = await client.GetAsync($"/claims/{Guid.NewGuid()}/audit");

        Assert.Empty(empty!);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task A_message_delivered_twice_stores_its_audit_trail_once()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var created = await Post(client, Assessed());

        var second = await client.PostAsJsonAsync("/intake/claims", Assessed(), ProposedClaim.JsonOptions);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(3, (await client.GetFromJsonAsync<List<AuditEntryDto>>($"/claims/{created.Id}/audit", Json))!.Count);
    }

    [Fact]
    public async Task An_oversized_audit_trail_or_assessment_is_refused_and_nothing_is_stored()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var tooManyCalls = Assessed("inbox/many.pdf", ClaimIntakeService.MaxAuditEntries + 1);
        var hugeArgument = Assessed("inbox/arg.pdf") with
        {
            AuditTrail = [new AuditEntry(Guid.Empty, "lookup_policy", new Dictionary<string, string> { ["q"] = new string('x', 400) }, "ok", 1, ApiFactory.Now)],
        };
        var tooManyFindings = Assessed("inbox/findings.pdf") with
        {
            Assessment = new ClaimAssessment("agent", [.. Enumerable.Repeat(new CoverageFinding(FindingKind.PossibleExclusion, "x", [Flood]), ClaimIntakeService.MaxFindings + 1)],
                ReviewRoute.SeniorReview, "x", []),
        };

        foreach (var claim in new[] { tooManyCalls, hugeArgument, tooManyFindings })
        {
            var response = await client.PostAsJsonAsync("/intake/claims", claim, ProposedClaim.JsonOptions);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/claims/{claim.ClaimId}")).StatusCode);
        }
    }

    // ----- the assessment on the claim detail -----

    [Fact]
    public async Task The_claim_detail_carries_the_findings_with_their_cited_clause_text_and_the_route()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var created = await Post(client, Assessed());

        var detail = await client.GetFromJsonAsync<ClaimDetail>($"/claims/{created.Id}", Json);

        var assessment = detail!.Assessment!;
        Assert.Equal(("rules", ReviewRoute.SeniorReview), (assessment.Assessor, assessment.Route));
        var finding = Assert.Single(assessment.Findings);
        Assert.Equal(FindingKind.PossibleExclusion, finding.Kind);
        Assert.Equal(Flood, Assert.Single(finding.Citations));
    }

    [Fact]
    public async Task A_claim_handed_over_without_an_assessment_has_none_and_deciding_it_does_not_touch_the_assessment()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var plain = await client.GetFromJsonAsync<ClaimDetail>($"/claims/{factory.Clean.Id}", Json);
        var created = await Post(client, Assessed());

        await client.PostAsJsonAsync($"/claims/{created.Id}/reject", new RejectRequest("a.patel", "Flood is excluded."));
        var after = await client.GetFromJsonAsync<ClaimDetail>($"/claims/{created.Id}", Json);

        Assert.Null(plain!.Assessment);
        Assert.Equal(ClaimStatus.Rejected, after!.Status);
        Assert.Equal(ReviewRoute.SeniorReview, after.Assessment!.Route);
    }

    // ----- GET /claims/history (the duplicate check reads it) -----

    [Fact]
    public async Task History_lists_the_claims_on_a_policy_with_ids_dates_and_amounts_but_no_names_or_text()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/claims/history?policyNumber=tm-cp-100001");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var items = JsonSerializer.Deserialize<List<ClaimHistoryItem>>(body, Json)!;
        Assert.Equal(3, items.Count); // Clean, warning and error; the rejected claim is left out.
        Assert.All(items, i => Assert.Equal(("TM-CP-100001", new DateOnly(2026, 11, 3), 12_500m), (i.PolicyNumber, i.LossDate, i.AmountGbp)));
        Assert.DoesNotContain(items, i => i.Id == factory.AlreadyRejected.Id);
        Assert.DoesNotContain("Brindle", body);
        Assert.DoesNotContain("pipe", body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?policyNumber=")]
    [InlineData("?policyNumber=TM-CP-1000000000000000000000000000000000000000000")]
    public async Task History_needs_a_policy_number(string query)
    {
        using var factory = new ApiFactory();

        var response = await factory.CreateClient().GetAsync($"/claims/history{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task History_for_another_policy_is_empty()
    {
        using var factory = new ApiFactory();

        var items = await factory.CreateClient().GetFromJsonAsync<List<ClaimHistoryItem>>("/claims/history?policyNumber=TM-CP-100002", Json);

        Assert.Empty(items!);
    }

    // ----- the Azure Table Storage audit log, against a fake table -----

    private sealed class FakeTable : IAuditTable
    {
        public int EnsureCalls { get; private set; }
        public List<TableEntity> Rows { get; } = [];

        public Task EnsureExistsAsync(CancellationToken cancellationToken)
        {
            EnsureCalls++;
            return Task.CompletedTask;
        }

        public Task AddAsync(TableEntity entity, CancellationToken cancellationToken)
        {
            Rows.Add(entity);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TableEntity>> QueryPartitionAsync(string partitionKey, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TableEntity>>([.. Rows.Where(r => r.PartitionKey == partitionKey).OrderBy(r => r.RowKey, StringComparer.Ordinal)]);
    }

    [Fact]
    public async Task The_table_audit_log_stores_one_row_per_call_in_a_partition_per_claim_and_reads_them_back_in_time_order()
    {
        var table = new FakeTable();
        var log = new TableStorageAuditLog(table);
        var claimId = Guid.NewGuid();
        var t0 = new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);
        // Written out of order, and two in the same instant.
        await log.AppendAsync(new AuditEntry(claimId, "search_policy_wording", new Dictionary<string, string> { ["query"] = "[text:5 chars]" }, "2 clauses", 3.2, t0.AddSeconds(1)), CancellationToken.None);
        await log.AppendAsync(new AuditEntry(claimId, "lookup_policy", new Dictionary<string, string> { ["policy_number"] = "TM-CP-100001" }, "found", 1.1, t0), CancellationToken.None);
        await log.AppendAsync(new AuditEntry(claimId, "check_duplicate", new Dictionary<string, string>(), "none", 0.4, t0.AddSeconds(1), false), CancellationToken.None);
        await log.AppendAsync(new AuditEntry(Guid.NewGuid(), "lookup_policy", new Dictionary<string, string>(), "other claim", 1, t0), CancellationToken.None);

        var entries = await log.ForClaimAsync(claimId, CancellationToken.None);

        Assert.Equal(3, entries.Count);
        Assert.Equal("lookup_policy", entries[0].Tool);
        Assert.Equal(new HashSet<string> { "search_policy_wording", "check_duplicate" }, entries.Skip(1).Select(e => e.Tool).ToHashSet());
        Assert.Equal("TM-CP-100001", entries[0].Arguments["policy_number"]);
        Assert.Equal((claimId, t0, 1.1, true), (entries[0].ClaimId, entries[0].Timestamp, entries[0].DurationMs, entries[0].Succeeded));
        Assert.False(entries.Single(e => e.Tool == "check_duplicate").Succeeded);
        Assert.All(table.Rows.Where(r => r.PartitionKey == claimId.ToString("D")), r => Assert.Matches(@"^\d{19}-[0-9a-f]{8}$", r.RowKey));
        Assert.Equal(1, table.EnsureCalls); // The table is created once, not on every call.
    }

    [Fact]
    public async Task The_table_audit_log_works_behind_the_intake_service_in_place_of_the_in_memory_one()
    {
        var table = new FakeTable();
        var service = new ClaimIntakeService(new InMemoryClaimRepository(), new TableStorageAuditLog(table), NullLogger<ClaimIntakeService>.Instance);

        var result = await service.AcceptAsync(Assessed(), CancellationToken.None);

        Assert.Equal(IntakeOutcome.Accepted, result.Outcome);
        Assert.Equal(3, table.Rows.Count);
        Assert.All(table.Rows, r => Assert.Equal(DocumentClaimId.ToString("D"), r.PartitionKey));
    }

    [Fact]
    public void Without_a_table_endpoint_the_audit_log_is_in_memory_and_with_one_it_needs_an_https_url()
    {
        static ServiceProvider Build(params (string, string)[] settings)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Item1, s.Item2))).Build();
            return new ServiceCollection().AddAuditLog(configuration).BuildServiceProvider();
        }

        using var local = Build();
        using var azure = Build(("Audit:Table:Endpoint", "https://account.table.core.windows.net"), ("Audit:Table:Name", "claimaudit"));

        Assert.IsType<InMemoryAuditLog>(local.GetRequiredService<IAuditLog>());
        Assert.IsType<TableStorageAuditLog>(azure.GetRequiredService<IAuditLog>());
        Assert.Throws<InvalidOperationException>(() => Build(("Audit:Table:Endpoint", "http://account.table.core.windows.net")));
    }

    // ----- the in-memory log -----

    [Fact]
    public async Task The_in_memory_log_keeps_claims_apart_and_sorts_by_time()
    {
        var log = new InMemoryAuditLog();
        var a = Guid.NewGuid();
        var t = ApiFactory.Now;
        await log.AppendAsync(new AuditEntry(a, "second", new Dictionary<string, string>(), "", 1, t.AddSeconds(2)), CancellationToken.None);
        await log.AppendAsync(new AuditEntry(a, "first", new Dictionary<string, string>(), "", 1, t), CancellationToken.None);
        await log.AppendAsync(new AuditEntry(Guid.NewGuid(), "other", new Dictionary<string, string>(), "", 1, t), CancellationToken.None);

        Assert.Equal(["first", "second"], (await log.ForClaimAsync(a, CancellationToken.None)).Select(e => e.Tool));
        Assert.Empty(await log.ForClaimAsync(Guid.NewGuid(), CancellationToken.None));
    }
}
