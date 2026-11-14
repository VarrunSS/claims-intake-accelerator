using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Data.Tables;
using ClaimsIntake.Api.Audit;
using ClaimsIntake.Api.Claims;
using ClaimsIntake.Api.Intake;
using ClaimsIntake.Api.Legacy;
using ClaimsIntake.Domain;
using ClaimsIntake.Domain.Assessment;
using ClaimsIntake.Domain.Intake;
using ClaimsIntake.Domain.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaimsIntake.Api.Tests;

/// <summary>SEC-04 (decision audit), REL-03 (one gate for both decisions) and REL-05 (the trail survives a partial failure).</summary>
public class DecisionAuditAndReliabilityTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static Task<IReadOnlyList<AuditEntry>> Trail(ApiFactory factory, Claim claim) =>
        factory.Audit.ForClaimAsync(claim.Id, default);

    // ----- SEC-04: every decision is audited, with the actor -----

    [Fact]
    public async Task Approving_a_claim_records_approved_then_submitted_with_the_actors_object_id()
    {
        using var factory = new ApiFactory();

        await factory.CreateClient().PostAsync($"/claims/{factory.Clean.Id}/approve", null);

        var trail = await Trail(factory, factory.Clean);
        Assert.Equal([DecisionAudit.Approved, DecisionAudit.Submitted], trail.Select(e => e.Tool));
        var approved = trail[0];
        Assert.Equal(TestAuth.HandlerOid, approved.Arguments["actor_oid"]);
        Assert.Equal(AppRoles.ClaimsHandler, approved.Arguments["role"]);
        Assert.Equal("NoAssessment", approved.Arguments["route"]);
        Assert.Equal("LC-000001", trail[1].Arguments["legacy_reference"]);
        // No names and no free text anywhere in a decision record.
        Assert.DoesNotContain(trail, e => e.Arguments.Values.Append(e.ResultSummary).Any(v => v.Contains("Patel") || v.Contains("Brindle")));
    }

    [Fact]
    public async Task A_senior_approval_records_the_senior_role_and_the_route_at_decision_time()
    {
        using var factory = new ApiFactory().WithRoutedClaims();

        await factory.CreateClient().WithBearer(TestAuth.Senior()).PostAsync($"/claims/{factory.SeniorRouted.Id}/approve", null);

        var approved = (await Trail(factory, factory.SeniorRouted)).Single(e => e.Tool == DecisionAudit.Approved);
        Assert.Equal((AppRoles.SeniorHandler, "SeniorReview", TestAuth.SeniorOid), (approved.Arguments["role"], approved.Arguments["route"], approved.Arguments["actor_oid"]));
    }

    [Fact]
    public async Task Rejecting_records_one_entry_with_the_reason_length_and_not_the_reason()
    {
        using var factory = new ApiFactory();

        await factory.CreateClient().PostAsJsonAsync($"/claims/{factory.Clean.Id}/reject", new RejectRequest("Duplicate of LC-000042, see Mr Smith."));

        var entry = Assert.Single(await Trail(factory, factory.Clean));
        Assert.Equal(DecisionAudit.Rejected, entry.Tool);
        Assert.Equal(TestAuth.HandlerOid, entry.Arguments["actor_oid"]);
        Assert.Equal("37", entry.Arguments["reason_length"]);
        Assert.DoesNotContain(entry.Arguments.Values.Append(entry.ResultSummary), v => v.Contains("Smith") || v.Contains("Duplicate"));
    }

    [Fact]
    public async Task A_failed_legacy_write_records_approved_and_submit_failed_and_the_retry_records_submitted()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        factory.Legacy.FailWith = new LegacyUnavailableException("down");

        var failed = await client.PostAsync($"/claims/{factory.Clean.Id}/approve", null);

        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.Equal([DecisionAudit.Approved, DecisionAudit.SubmitFailed], (await Trail(factory, factory.Clean)).Select(e => e.Tool));
        Assert.Equal("unavailable", (await Trail(factory, factory.Clean))[1].Arguments["outcome"]);

        factory.Legacy.FailWith = null;
        await client.PostAsync($"/claims/{factory.Clean.Id}/approve", null);

        var trail = await Trail(factory, factory.Clean);
        Assert.Equal([DecisionAudit.Approved, DecisionAudit.SubmitFailed, DecisionAudit.Submitted], trail.Select(e => e.Tool).OrderBy(t => t == DecisionAudit.Submitted ? 2 : t == DecisionAudit.SubmitFailed ? 1 : 0));
        Assert.Equal(1, trail.Count(e => e.Tool == DecisionAudit.Approved)); // The approval is not recorded twice by the retry.
    }

    [Fact]
    public async Task A_refused_legacy_write_is_audited_as_refused()
    {
        using var factory = new ApiFactory();
        factory.Legacy.FailWith = new LegacyRejectedException("no");

        await factory.CreateClient().PostAsync($"/claims/{factory.Clean.Id}/approve", null);

        var failed = (await Trail(factory, factory.Clean)).Single(e => e.Tool == DecisionAudit.SubmitFailed);
        Assert.Equal("refused", failed.Arguments["outcome"]);
        Assert.False(failed.Succeeded);
    }

    [Fact]
    public async Task A_replayed_approval_appends_nothing()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        await client.PostAsync($"/claims/{factory.Clean.Id}/approve", null);
        var before = await Trail(factory, factory.Clean);

        var replay = await client.WithBearer(TestAuth.Senior()).PostAsync($"/claims/{factory.Clean.Id}/approve", null);
        await client.PostAsync($"/claims/{factory.Clean.Id}/approve", null);

        Assert.True(replay.Headers.Contains("Idempotent-Replay"));
        Assert.Equal(before.Select(e => (e.Tool, e.Timestamp)), (await Trail(factory, factory.Clean)).Select(e => (e.Tool, e.Timestamp)));
    }

    [Fact]
    public async Task A_refused_or_forbidden_decision_records_nothing()
    {
        using var factory = new ApiFactory().WithRoutedClaims();
        var client = factory.CreateClient();

        await client.PostAsync($"/claims/{factory.SeniorRouted.Id}/approve", null); // 403: wrong role
        await client.PostAsync($"/claims/{factory.WithError.Id}/approve", null); // 409: blocking issue
        await client.PostAsJsonAsync($"/claims/{factory.AlreadyRejected.Id}/reject", new RejectRequest("Again.")); // 409: already decided

        Assert.Empty(await Trail(factory, factory.SeniorRouted));
        Assert.Empty(await Trail(factory, factory.WithError));
        Assert.Empty(await Trail(factory, factory.AlreadyRejected));
    }

    [Fact]
    public async Task G1_every_legacy_reference_has_an_approved_record_for_the_same_claim_and_actor()
    {
        // Guardrail G1: "every write to the legacy system references an approval record".
        using var factory = new ApiFactory().WithRoutedClaims();
        var handler = factory.CreateClient();
        var senior = factory.CreateClient().WithBearer(TestAuth.Senior());
        await handler.PostAsync($"/claims/{factory.Clean.Id}/approve", null);
        await handler.PostAsync($"/claims/{factory.HandlerRouted.Id}/approve", null);
        await senior.PostAsync($"/claims/{factory.SeniorRouted.Id}/approve", null);
        await handler.PostAsync($"/claims/{factory.WithWarning.Id}/approve", null);

        var submitted = (await factory.Repository.ListAsync(ClaimStatus.Submitted, default)).ToList();

        Assert.Equal(4, submitted.Count);
        foreach (var claim in submitted)
        {
            var trail = await Trail(factory, claim);
            var approved = Assert.Single(trail, e => e.Tool == DecisionAudit.Approved);
            Assert.Equal(claim.DecidedBy, approved.Arguments["actor_oid"]);
            Assert.True(approved.Timestamp <= trail.Single(e => e.Tool == DecisionAudit.Submitted).Timestamp);
            Assert.Equal(claim.LegacyReference, trail.Single(e => e.Tool == DecisionAudit.Submitted).Arguments["legacy_reference"]);
        }
    }

    [Fact]
    public async Task Decisions_appear_in_the_claims_audit_endpoint_next_to_the_tool_calls()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        await client.PostAsync($"/claims/{factory.Clean.Id}/approve", null);

        var entries = await client.GetFromJsonAsync<List<AuditEntryDto>>($"/claims/{factory.Clean.Id}/audit", Json);

        Assert.Contains(entries!, e => e.Tool == DecisionAudit.Approved && e.Arguments["actor_oid"] == TestAuth.HandlerOid);
    }

    // ----- REL-03: one gate for approve and reject -----

    private sealed class BlockingLegacy : ILegacyClaimWriter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<LegacySubmission> SubmitAsync(Claim claim, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task;
            return new LegacySubmission("LC-000001", false);
        }
    }

    [Fact]
    public async Task A_reject_that_lands_while_an_approval_is_writing_to_legacy_gets_409_and_the_approval_completes_consistently()
    {
        var blocking = new BlockingLegacy();
        using var factory = new ApiFactory();
        using var app = factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<ILegacyClaimWriter>();
            services.AddSingleton<ILegacyClaimWriter>(blocking);
        }));
        var client = app.CreateClient();

        var approve = client.PostAsync($"/claims/{factory.Clean.Id}/approve", null);
        await blocking.Started.Task; // The approval is now inside the legacy call, holding the claim's gate.
        var reject = client.PostAsJsonAsync($"/claims/{factory.Clean.Id}/reject", new RejectRequest("Changed my mind."));
        await Task.Delay(100);
        Assert.False(reject.IsCompleted); // It waits for the gate; it cannot interleave.
        blocking.Release.SetResult();

        var approved = await approve;
        var rejected = await reject;

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode); // No 500, no half-rejected claim.
        Assert.Equal(ClaimStatus.Submitted, factory.Clean.Status);
        Assert.Equal("LC-000001", factory.Clean.LegacyReference);
        Assert.Equal([DecisionAudit.Approved, DecisionAudit.Submitted], (await Trail(factory, factory.Clean)).Select(e => e.Tool));
    }

    [Fact]
    public async Task Concurrent_approve_and_reject_always_leave_one_consistent_outcome()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var tasks = Enumerable.Range(0, 8).Select(i => i % 2 == 0
            ? client.PostAsync($"/claims/{factory.Clean.Id}/approve", null)
            : client.PostAsJsonAsync($"/claims/{factory.Clean.Id}/reject", new RejectRequest("No.")));
        var responses = await Task.WhenAll(tasks);

        Assert.DoesNotContain(responses, r => r.StatusCode == HttpStatusCode.InternalServerError);
        Assert.True(factory.Clean.Status is ClaimStatus.Submitted or ClaimStatus.Rejected);
        Assert.True(factory.Legacy.Created <= 1);
        Assert.Equal(factory.Clean.Status == ClaimStatus.Submitted, factory.Legacy.Created == 1); // A rejected claim was never written.
    }

    // ----- REL-05: the trail survives a failed write and a redelivery -----

    private sealed class FakeTable : IAuditTable
    {
        public List<TableEntity> Rows { get; } = [];
        public int Calls { get; private set; }
        public int FailOnCall { get; set; }

        public Task EnsureExistsAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpsertAsync(TableEntity entity, CancellationToken cancellationToken)
        {
            if (++Calls == FailOnCall)
            {
                throw new InvalidOperationException("Table storage is throttling.");
            }

            Rows.RemoveAll(r => r.PartitionKey == entity.PartitionKey && r.RowKey == entity.RowKey);
            Rows.Add(entity);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TableEntity>> QueryPartitionAsync(string partitionKey, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TableEntity>>([.. Rows.Where(r => r.PartitionKey == partitionKey).OrderBy(r => r.RowKey, StringComparer.Ordinal)]);
    }

    private static ProposedClaim WithTrail(int entries)
    {
        var t = ApiFactory.Now;
        return new ProposedClaim("inbox/trail.pdf", t,
            [new ExtractedField(ClaimFields.PolicyNumber, "TM-CP-100001", 0.9)], [], ["Policy number: TM-CP-100001"], null,
            [.. Enumerable.Range(0, entries).Select(i => new AuditEntry(Guid.Empty, "search_policy_wording",
                new Dictionary<string, string> { ["query"] = "[text:5 chars]" }, $"result {i}", 1, t))]); // Same tick on purpose.
    }

    [Fact]
    public async Task A_table_that_fails_on_the_second_append_then_a_redelivery_leaves_the_full_trail_once_and_one_claim()
    {
        var table = new FakeTable { FailOnCall = 2 };
        var repository = new InMemoryClaimRepository();
        var service = new ClaimIntakeService(repository, new TableStorageAuditLog(table), NullLogger<ClaimIntakeService>.Instance);
        var message = WithTrail(4);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AcceptAsync(message, default)); // Service Bus abandons the message.
        Assert.Empty(await repository.ListAsync(null, default)); // The claim was not added before its trail (REL-05).

        var redelivered = await service.AcceptAsync(message, default);

        Assert.Equal(IntakeOutcome.Accepted, redelivered.Outcome);
        var rows = await new TableStorageAuditLog(table).ForClaimAsync(message.ClaimId, default);
        Assert.Equal(4, rows.Count);
        Assert.Equal(4, rows.Select(r => r.ResultSummary).Distinct().Count()); // Nothing lost, nothing duplicated, though all four share a tick.
        Assert.Single(await repository.ListAsync(null, default));

        // A third delivery changes nothing.
        Assert.Equal(IntakeOutcome.Duplicate, (await service.AcceptAsync(message, default)).Outcome);
        Assert.Equal(4, table.Rows.Count);
    }

    [Fact]
    public async Task Row_keys_are_deterministic_so_the_same_entry_always_lands_on_the_same_row()
    {
        var entry = new AuditEntry(Guid.NewGuid(), "lookup_policy", new Dictionary<string, string>(), "found", 1, ApiFactory.Now, true, "t000");

        var first = TableStorageAuditLog.ToEntity(entry);
        var second = TableStorageAuditLog.ToEntity(entry with { DurationMs = 99 });

        Assert.Equal(first.RowKey, second.RowKey);
        Assert.Matches(@"^\d{19}-t000$", first.RowKey);
    }
}
