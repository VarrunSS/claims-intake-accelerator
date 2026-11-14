using System.Diagnostics;
using ClaimsIntake.Domain;
using ClaimsIntake.Domain.Assessment;
using ClaimsIntake.Domain.Intake;
using ClaimsIntake.Pipeline.Assessment;
using ClaimsIntake.Pipeline.Legacy;
using ClaimsIntake.Pipeline.Policy;
using ClaimsIntake.Pipeline.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaimsIntake.Pipeline.Tests;

/// <summary>The assessment as it runs inside the pipeline, over the real sample documents and the real wordings.</summary>
public class AssessmentPipelineTests
{
    private static async Task<ProposedClaim> Run(string sample, IExistingClaims? existing = null, RecordingSink? sink = null)
    {
        var result = await Assessing.Pipeline(existing: existing, sink: sink).ProcessAsync(Docs.Sample(sample), CancellationToken.None);
        return result.Claim;
    }

    private static string[] Cited(ProposedClaim claim, FindingKind kind) =>
        [.. claim.Assessment!.Findings.Where(f => f.Kind == kind).SelectMany(f => f.Citations).Select(c => $"{c.WordingId}/{c.ClauseRef}")];

    [Theory]
    [InlineData("01-water-damage-clean.pdf", "TM-PW-CC-2026/3.2")]
    [InlineData("02-theft-clean.pdf", "TM-PW-CC-2026/3.3")]
    [InlineData("06-email-fire.txt", "TM-PW-OF-2026/3.1")]
    [InlineData("07-broker-email.eml", "TM-PW-CC-2026/3.5")]
    public async Task The_clean_samples_are_covered_with_no_exclusion_and_go_straight_to_a_handler(string sample, string clause)
    {
        var claim = await Run(sample);

        Assert.Equal(ReviewRoute.StraightToHandler, claim.Assessment!.Route);
        Assert.Equal([clause], Cited(claim, FindingKind.CoverageClause));
        Assert.Single(claim.Assessment.Findings);
        Assert.Empty(claim.Assessment.Notes);
    }

    [Fact]
    public async Task The_flood_sample_triggers_the_flood_exclusion_of_its_wording()
    {
        var claim = await Run("09-flood-office-exclusion.pdf");

        Assert.Equal(["TM-PW-OF-2026/4.1"], Cited(claim, FindingKind.PossibleExclusion));
        Assert.Equal(ReviewRoute.SeniorReview, claim.Assessment!.Route);
    }

    [Fact]
    public async Task The_unoccupied_and_late_sample_triggers_an_exclusion_and_a_notification_breach()
    {
        var claim = await Run("10-unoccupied-late-notification.pdf");

        Assert.Equal(["TM-PW-CC-2026/4.3"], Cited(claim, FindingKind.PossibleExclusion));
        Assert.Equal(["TM-PW-CC-2026/5.1"], Cited(claim, FindingKind.NotificationBreach));
        Assert.Contains("40 days", claim.Assessment!.Findings.Single(f => f.Kind == FindingKind.NotificationBreach).Summary);
        Assert.Equal(ReviewRoute.SeniorReview, claim.Assessment.Route);
    }

    [Fact]
    public async Task The_chasing_email_is_a_possible_duplicate_of_the_claim_already_in_the_review_store()
    {
        var firstId = ClaimIds.ForDocument("inbox/01-water-damage-clean.pdf");
        var existing = new FakeExistingClaims(new ExistingClaim(firstId.ToString(), "review", "TM-CP-100001", new DateOnly(2026, 11, 3), 12_500m, "NeedsReview"));

        var claim = await Run("11-duplicate-water-damage-email.txt", existing);

        var duplicate = Assert.Single(claim.Assessment!.Findings, f => f.Kind == FindingKind.PossibleDuplicate);
        Assert.Equal(firstId.ToString(), duplicate.RelatedClaimRef);
        Assert.Equal(ReviewRoute.SeniorReview, claim.Assessment.Route);
        Assert.Equal(["TM-PW-CC-2026/3.2"], Cited(claim, FindingKind.CoverageClause)); // Still covered, but a person should look.
    }

    [Fact]
    public async Task At_least_two_samples_trigger_exclusions()
    {
        var withExclusion = new List<string>();
        foreach (var sample in Directory.EnumerateFiles(Docs.SamplesFolder).Select(Path.GetFileName).Order())
        {
            var claim = await Run(sample!);
            if (claim.Assessment!.Findings.Any(f => f.Kind == FindingKind.PossibleExclusion))
            {
                withExclusion.Add(sample!);
            }
        }

        Assert.Equal(["09-flood-office-exclusion.pdf", "10-unoccupied-late-notification.pdf"], withExclusion);
    }

    [Fact]
    public async Task A_claim_with_no_policy_number_is_handed_off_with_an_assessment_that_says_cover_was_not_assessed()
    {
        var claim = await Run("03-missing-policy-number.pdf");

        Assert.Empty(claim.Assessment!.Findings);
        Assert.Contains("could not be identified", Assert.Single(claim.Assessment.Notes));
        Assert.Empty(claim.AuditTrail!);
    }

    [Fact]
    public async Task The_assessment_and_audit_trail_travel_in_the_hand_off_message_and_survive_json()
    {
        var sink = new RecordingSink();
        await Run("10-unoccupied-late-notification.pdf", sink: sink);

        var sent = Assert.Single(sink.Claims);
        var back = ProposedClaim.FromJson(sent.ToJson());

        Assert.Equal(sent.Assessment!.Findings.Count, back.Assessment!.Findings.Count);
        Assert.Equal(sent.Assessment.Route, back.Assessment.Route);
        Assert.Equal(sent.AuditTrail!.Select(a => a.Tool), back.AuditTrail!.Select(a => a.Tool));
        Assert.Contains("\"kind\":\"PossibleExclusion\"", sent.ToJson());
        Assert.All(back.AuditTrail!, a => Assert.Equal(sent.ClaimId, a.ClaimId));
    }

    [Fact]
    public async Task The_audit_trail_records_every_tool_call_with_the_claim_id_and_no_personal_data()
    {
        var claim = await Run("10-unoccupied-late-notification.pdf");

        var trail = claim.AuditTrail!;
        Assert.Equal([ToolNames.LookupPolicy, ToolNames.SearchPolicyWording, ToolNames.SearchPolicyWording, ToolNames.SearchPolicyWording,
            ToolNames.CheckDuplicate, ToolNames.ProposeClaim], trail.Select(t => t.Tool));
        Assert.All(trail, t => Assert.Equal(claim.ClaimId, t.ClaimId));
        var everything = string.Join('\n', trail.SelectMany(t => t.Arguments.Select(a => $"{a.Key}={a.Value}").Append(t.ResultSummary)));
        foreach (var personal in new[] { "Ashdown", "Veterinary", "refitted", "pipe burst", "closed branch", "surgery" })
        {
            Assert.DoesNotContain(personal, everything, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("policy_number=TM-CP-100008", everything);
    }

    [Fact]
    public async Task The_pipeline_runs_an_assess_span_after_validate_and_before_the_hand_off_with_ids_only()
    {
        var documentId = $"inbox/{Guid.NewGuid():N}-01-water-damage-clean.pdf";
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == PipelineTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (spans) { spans.Add(a); } },
        };
        ActivitySource.AddActivityListener(listener);

        await Assessing.Pipeline().ProcessAsync(Docs.Sample("01-water-damage-clean.pdf") with { DocumentId = documentId }, CancellationToken.None);

        List<string> stages;
        lock (spans)
        {
            var claimId = ClaimIds.ForDocument(documentId).ToString();
            stages = [.. spans.Where(a => a.GetTagItem("documentId") as string == documentId || a.GetTagItem("claimId") as string == claimId)
                .OrderBy(a => a.StartTimeUtc).ThenBy(a => a.Id).Select(a => a.OperationName)];
        }

        Assert.Contains("intake.assess", stages);
        Assert.Equal(5, stages.Count(s => s.StartsWith("tool.", StringComparison.Ordinal)));
        Assert.True(stages.IndexOf("intake.validate") < stages.IndexOf("intake.assess"));
        Assert.True(stages.IndexOf("intake.assess") < stages.IndexOf("intake.handoff"));
    }

    [Fact]
    public async Task An_assessor_that_fails_does_not_lose_the_claim_and_nothing_sensitive_is_logged()
    {
        var sink = new RecordingSink();
        var logger = new ListLogger<ClaimIntakePipeline>();
        var runnerLogger = new ListLogger<AssessmentRunner>();

        var result = await Assessing.Pipeline(new ThrowingAssessor(), sink: sink, logger: logger, runnerLogger: runnerLogger)
            .ProcessAsync(Docs.Sample("01-water-damage-clean.pdf"), CancellationToken.None);

        var claim = Assert.Single(sink.Claims);
        Assert.Same(claim, result.Claim);
        Assert.Equal(ReviewRoute.SeniorReview, claim.Assessment!.Route);
        Assert.Contains("InvalidOperationException", Assert.Single(claim.Assessment.Notes));
        Assert.Equal(ToolNames.LookupPolicy, Assert.Single(claim.AuditTrail!).Tool); // The call made before the failure is kept.
        var logged = string.Join('\n', logger.Lines.Concat(runnerLogger.Lines));
        Assert.Contains("InvalidOperationException", logged);
        Assert.DoesNotContain("Brindle", logged);
        Assert.DoesNotContain("TM-CP-100001", logged);
        Assert.DoesNotContain("water damage", logged);
    }

    [Fact]
    public async Task A_pipeline_without_an_assessor_hands_off_a_claim_with_no_assessment()
    {
        var sink = new RecordingSink();
        await Docs.Pipeline(sink: sink).ProcessAsync(Docs.Sample("01-water-damage-clean.pdf"), CancellationToken.None);

        var claim = Assert.Single(sink.Claims);
        Assert.Null(claim.Assessment);
        Assert.Null(claim.AuditTrail);
    }

    [Fact]
    public async Task The_assessor_runs_before_the_hand_off_and_the_hand_off_failing_still_fails_the_document()
    {
        var sink = new RecordingSink { FailWith = new HttpRequestException("api down") };

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Assessing.Pipeline(sink: sink).ProcessAsync(Docs.Sample("01-water-damage-clean.pdf"), CancellationToken.None));
    }
}

public class AssessmentCompositionTests
{
    private static ServiceProvider Build(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddClaimIntakePipeline(configuration);
        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public void By_default_the_local_search_and_the_rule_based_assessor_are_used_with_no_azure_account()
    {
        using var provider = Build();

        Assert.IsType<Bm25PolicyWordingSearch>(provider.GetRequiredService<IPolicyWordingSearch>());
        Assert.IsType<RuleBasedClaimAssessor>(provider.GetRequiredService<IClaimAssessor>());
        Assert.IsType<CompositeExistingClaims>(provider.GetRequiredService<IExistingClaims>());
        Assert.NotNull(provider.GetRequiredService<AssessmentRunner>());
        Assert.Equal(84, ((Bm25PolicyWordingSearch)provider.GetRequiredService<IPolicyWordingSearch>()).ClauseCount);
    }

    [Fact]
    public void The_assessor_can_be_switched_off()
    {
        using var provider = Build(("Intake:Assessor", "None"));

        Assert.Null(provider.GetService<AssessmentRunner>());
        Assert.NotNull(provider.GetRequiredService<ClaimIntakePipeline>());
    }

    [Fact]
    public async Task The_agent_assessor_and_azure_ai_search_are_chosen_by_configuration_and_need_no_keys()
    {
        await using var provider = Build(
            ("Intake:Assessor", "agent"),
            ("Intake:PolicySearch", "AzureAiSearch"),
            ("Intake:AzureAiSearch:Endpoint", "https://search-example.search.windows.net"),
            ("Intake:Foundry:Endpoint", "https://example.openai.azure.com/"),
            ("Intake:Foundry:Deployment", "gpt-assessor"),
            ("Intake:Foundry:EmbeddingDeployment", "text-embedding-3-small"),
            ("Intake:ManagedIdentityClientId", "00000000-0000-0000-0000-000000000000"));

        Assert.IsType<AgentClaimAssessor>(provider.GetRequiredService<IClaimAssessor>());
        Assert.IsType<AzureAiSearchPolicyWordingSearch>(provider.GetRequiredService<IPolicyWordingSearch>());
        Assert.NotNull(provider.GetRequiredService<IChatClient>());
        Assert.NotNull(provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());
        Assert.NotNull(provider.GetRequiredService<ClaimIntakePipeline>());
    }

    [Theory]
    [InlineData("Intake:Assessor", "Magic")]
    [InlineData("Intake:PolicySearch", "Grep")]
    public void An_unknown_choice_is_refused_at_start_up(string key, string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build((key, value)));

        Assert.Contains(key, ex.Message);
    }

    [Theory]
    [InlineData("Intake:Assessor", "Agent", "Intake:Foundry:Endpoint")]
    [InlineData("Intake:PolicySearch", "AzureAiSearch", "Intake:Foundry:Endpoint")]
    public void An_azure_choice_without_its_settings_is_refused_at_start_up(string key, string value, string missing)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build((key, value)));

        Assert.Contains("Intake:", ex.Message);
        Assert.True(ex.Message.Contains(missing, StringComparison.Ordinal), ex.Message);
    }

    [Fact]
    public void Azure_search_also_needs_an_embedding_deployment_and_a_missing_wordings_folder_fails_at_first_use()
    {
        Assert.Throws<InvalidOperationException>(() => Build(
            ("Intake:PolicySearch", "AzureAiSearch"),
            ("Intake:AzureAiSearch:Endpoint", "https://search-example.search.windows.net"),
            ("Intake:Foundry:Endpoint", "https://example.openai.azure.com/")));
        var noSearch = Assert.Throws<InvalidOperationException>(() => Build(
            ("Intake:PolicySearch", "AzureAiSearch"), ("Intake:Foundry:Endpoint", "https://example.openai.azure.com/")));
        Assert.Contains("Intake:AzureAiSearch:Endpoint", noSearch.Message);

        using var provider = Build(("Intake:Wordings:Folder", Path.Combine(Path.GetTempPath(), "no-such-wordings")));
        Assert.Throws<DirectoryNotFoundException>(() => provider.GetRequiredService<IPolicyWordingSearch>());
    }
}
