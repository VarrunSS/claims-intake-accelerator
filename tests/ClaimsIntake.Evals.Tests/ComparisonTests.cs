using System.Text.Json;
using Azure.Core;
using ClaimsIntake.Evals;
using Microsoft.Extensions.AI;

namespace ClaimsIntake.Evals.Tests;

public class ModelRegistryTests
{
    private const string Valid = """
        { "models": [ { "id": "m1", "displayName": "M", "kind": "azure-openai", "endpoint": "https://x.openai.azure.com/", "deployment": "d1", "enabled": true,
          "price": { "currency": "GBP", "inputPer1M": 1.5, "outputPer1M": 6, "sourceUrl": "https://example.test", "asOf": "2026-11-14", "status": "verify-before-use" } } ] }
        """;

    private static string Mutate(string from, string to) => Valid.Replace(from, to, StringComparison.Ordinal);

    [Fact]
    public void A_valid_registry_parses()
    {
        var registry = ModelRegistry.Parse(Valid);

        var m = Assert.Single(registry.Models);
        Assert.Equal(("m1", "azure-openai", "d1", true), (m.Id, m.Kind, m.Deployment, m.Enabled));
        Assert.Equal(1.5m, m.Price.InputPer1M);
        Assert.False(m.Price.IsVerified);
    }

    [Fact]
    public void The_committed_registry_has_three_disabled_placeholders_with_unverified_prices()
    {
        var path = Path.Combine(Path.GetDirectoryName(Repo.Dataset)!, "models.json");

        var registry = ModelRegistry.Load(path);

        Assert.Equal(3, registry.Models.Count);
        Assert.All(registry.Models, m =>
        {
            Assert.False(m.Enabled);
            Assert.False(m.Price.IsVerified);
            Assert.Equal("GBP", m.Price.Currency);
            Assert.StartsWith("https://", m.Price.SourceUrl);
        });
        Assert.Equal(["azure-openai", "foundry-anthropic", "azure-ai-inference"], registry.Models.Select(m => m.Kind));
        Assert.DoesNotContain("apiKey", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase); // No keys in files.
    }

    [Theory]
    [InlineData("azure-openai", "openai-compatible", "kind must be one of")]
    [InlineData("\"inputPer1M\": 1.5", "\"inputPer1M\": -1", "cannot be negative")]
    [InlineData("\"GBP\"", "\"USD\"", "must be GBP")]
    [InlineData("2026-11-14", "14/11/2026", "yyyy-MM-dd")]
    [InlineData("verify-before-use", "probably", "status must be")]
    [InlineData("\"deployment\": \"d1\"", "\"deployment\": \"\"", "deployment is required")]
    [InlineData("https://x.openai.azure.com/", "https://<resource>.openai.azure.com/", "placeholder endpoint")]
    public void A_bad_entry_is_refused_with_a_reason(string from, string to, string reason)
    {
        var ex = Assert.Throws<InvalidDataException>(() => ModelRegistry.Parse(Mutate(from, to)));

        Assert.Contains(reason, ex.Message);
    }

    [Fact]
    public void A_duplicate_id_is_refused()
    {
        var one = Valid[Valid.IndexOf('{', 3)..Valid.LastIndexOf('}')];
        var entry = one[..(one.LastIndexOf('}') + 1)];

        Assert.Throws<InvalidDataException>(() => ModelRegistry.Parse($"{{ \"models\": [ {entry}, {entry} ] }}"));
    }

    [Fact]
    public void Malformed_json_is_refused()
    {
        Assert.ThrowsAny<JsonException>(() => ModelRegistry.Parse("{ nope"));
    }

    [Fact]
    public void The_fakes_are_found_only_with_the_dev_flag_and_a_real_id_is_found_without_it()
    {
        var registry = ModelRegistry.Parse(Valid);

        Assert.Equal("m1", registry.Find("M1", allowFakes: false).Id);
        var ex = Assert.Throws<ArgumentException>(() => registry.Find("fake-oracle", allowFakes: false));
        Assert.Contains("--dev-fakes", ex.Message);
        Assert.True(registry.Find("fake-oracle", allowFakes: true).IsFake);
        Assert.Throws<ArgumentException>(() => registry.Find("nope", allowFakes: true));
    }

    [Fact]
    public void A_disabled_model_cannot_be_turned_into_a_client()
    {
        var disabled = ModelRegistry.Parse(Mutate("\"enabled\": true", "\"enabled\": false")).Models[0];

        var ex = Assert.Throws<InvalidOperationException>(() => ChatClientFactory.Create(disabled, new StubCredential()));

        Assert.Contains("not enabled", ex.Message);
    }

    [Theory]
    [InlineData("azure-openai", "https://r.openai.azure.com/")]
    [InlineData("azure-ai-inference", "https://r.services.ai.azure.com/models")]
    [InlineData("foundry-anthropic", "my-foundry-resource")]
    public void Each_kind_builds_a_chat_client_with_no_network_call(string kind, string endpoint)
    {
        var price = new ModelPrice("GBP", 1m, 1m, "https://example.test", "2026-11-14", "verify-before-use");
        var entry = new ModelEntry("m", "M", kind, endpoint, "deployment", true, price, null);

        using var client = ChatClientFactory.Create(entry, new StubCredential());

        Assert.NotNull(client);
    }

    private sealed class StubCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Constructing a client must not ask for a token.");

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Constructing a client must not ask for a token.");
    }
}

public class RecommendationRuleTests
{
    private static GateResult Gates(bool pass, params string[] failing) =>
        new(pass
            ? [new GateCheck("ok", ">= 0", 1, true, "ok")]
            : [.. failing.Select(f => new GateCheck(f, ">= 1", 0, false, "too low"))]);

    private static CompareRow Row(
        string label, bool passes = true, double? cost = 0.01, double p95 = 1000, double malformed = 0, RowKind kind = RowKind.Model, bool unverified = false)
    {
        var info = new RunInfo(label, label, "azure-openai", kind == RowKind.Fake, [], new ModelPrice("GBP", 1m, 1m, "x", "2026-11-14", "verify-before-use"));
        var summary = Summariser.Summarise(label, "test", "llm", "agent", [], info) with
        {
            Latency = [new LatencySummary(Summariser.Total, 36, p95 / 2, p95, p95 / 2)],
            Model = new ModelUsageSummary(10, 1, 1, false, cost, cost * 1000, !unverified, 0, 0, 0, 0, 0, malformed, 0, 1, 2),
            Metrics = new Dictionary<string, double?> { ["cost_per_claim_gbp"] = cost },
        };
        return new CompareRow(label, kind, label, "v1", summary, passes ? Gates(true) : Gates(false, "route_accuracy"), null, unverified);
    }

    [Fact]
    public void The_cheapest_config_that_passes_every_gate_is_picked()
    {
        var result = Recommender.Recommend([Row("expensive", cost: 0.05), Row("cheap", cost: 0.01), Row("middle", cost: 0.02)]);

        Assert.Equal("cheap", result.Chosen);
        Assert.False(result.Demonstration);
    }

    [Fact]
    public void A_cheaper_config_that_fails_a_gate_is_not_picked_and_the_failure_is_named()
    {
        var result = Recommender.Recommend([Row("cheap-but-wrong", passes: false, cost: 0.001), Row("good", cost: 0.04)]);

        Assert.Equal("good", result.Chosen);
        Assert.Contains(result.Explanation, l => l.Contains("cheap-but-wrong") && l.Contains("route_accuracy"));
    }

    [Fact]
    public void A_cost_tie_is_broken_by_p95_latency()
    {
        var result = Recommender.Recommend([Row("slow", cost: 0.01, p95: 9000), Row("fast", cost: 0.01005, p95: 2000)]);

        Assert.Equal("fast", result.Chosen); // 0.00005 apart is inside the £0.0001 tie band.
        Assert.Contains(result.Explanation, l => l.Contains("tie"));
    }

    [Fact]
    public void A_cost_gap_wider_than_the_tie_band_is_not_a_tie_so_latency_is_ignored()
    {
        var result = Recommender.Recommend([Row("slow-cheap", cost: 0.0100, p95: 9000), Row("fast-dearer", cost: 0.0102, p95: 100)]);

        Assert.Equal("slow-cheap", result.Chosen);
    }

    [Fact]
    public void Equal_cost_and_latency_fall_back_to_malformed_rate_then_label()
    {
        Assert.Equal("b", Recommender.Recommend([Row("a", malformed: 0.04), Row("b", malformed: 0.01)]).Chosen);
        Assert.Equal("a", Recommender.Recommend([Row("b"), Row("a")]).Chosen);
    }

    [Fact]
    public void No_candidate_passing_means_no_recommendation()
    {
        var result = Recommender.Recommend([Row("a", passes: false), Row("b", passes: false)]);

        Assert.Null(result.Chosen);
        Assert.Contains(result.Explanation, l => l.Contains("no recommendation"));
    }

    [Fact]
    public void The_baseline_and_not_yet_run_rows_are_never_candidates()
    {
        var baseline = Row("baseline", cost: 0, kind: RowKind.Baseline);
        var notRun = new CompareRow("gpt-mid", RowKind.NotRun, "GPT", "v1", null, null, "not deployed", false);

        var result = Recommender.Recommend([baseline, notRun]);

        Assert.Null(result.Chosen);
        Assert.Contains("not yet run against real models", string.Join(' ', result.Explanation));
        Assert.Equal("real", Recommender.Recommend([baseline, notRun, Row("real", cost: 0.02)]).Chosen); // Not the £0 baseline.
    }

    [Fact]
    public void A_candidate_with_no_measurable_cost_is_not_eligible()
    {
        var result = Recommender.Recommend([Row("no-cost", cost: null), Row("costed", cost: 0.09)]);

        Assert.Equal("costed", result.Chosen);
    }

    [Fact]
    public void Fake_rows_are_used_only_when_no_real_model_ran_and_then_the_pick_is_labelled_a_demonstration()
    {
        var fakesOnly = Recommender.Recommend([Row("fake-a", kind: RowKind.Fake, cost: 0.002), Row("fake-b", kind: RowKind.Fake, cost: 0.004)]);
        var mixed = Recommender.Recommend([Row("fake-a", kind: RowKind.Fake, cost: 0.002), Row("real", cost: 0.03)]);

        Assert.Equal("fake-a", fakesOnly.Chosen);
        Assert.True(fakesOnly.Demonstration);
        Assert.Contains("FAKE", string.Join(' ', fakesOnly.Explanation));
        Assert.Equal("real", mixed.Chosen);
        Assert.False(mixed.Demonstration);
    }

    [Fact]
    public void An_unverified_price_is_flagged_on_the_pick()
    {
        var result = Recommender.Recommend([Row("m", unverified: true)]);

        Assert.Contains("verify-before-use", string.Join(' ', result.Explanation));
    }

    [Fact]
    public void The_rules_are_the_same_on_every_call_so_the_pick_does_not_depend_on_row_order()
    {
        var rows = new[] { Row("c", cost: 0.03), Row("a", cost: 0.01), Row("b", cost: 0.01, p95: 10) };

        Assert.Equal(Recommender.Recommend(rows).Chosen, Recommender.Recommend([.. rows.Reverse()]).Chosen);
        Assert.Equal("b", Recommender.Recommend(rows).Chosen);
    }
}

public class CompareEndToEndTests : IDisposable
{
    private readonly string _out = Path.Combine(Path.GetTempPath(), "m5-compare-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_out))
        {
            Directory.Delete(_out, recursive: true);
        }
    }

    private async Task<(int Exit, string Output, string Error)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await EvalCli.RunAsync(args, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Compare_with_the_fakes_runs_the_whole_pipeline_and_the_rules_pick_the_oracle()
    {
        var (exit, output, error) = await Run("compare", "--split", "test", "--dev-fakes", "--models", "fake-oracle,fake-noisy", "--run-id", "e2e", "--out", _out);

        Assert.True(exit == 0, error + output);
        var markdown = await File.ReadAllTextAsync(Path.Combine(_out, "e2e", "comparison.md"));
        var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_out, "e2e", "comparison.json"))).RootElement;

        // The labels are in the table itself (two fake rows in the results table, two in the call detail), and the report says plainly that no real model has run.
        Assert.Contains("Not yet run against real models", markdown);
        Assert.Equal(4, markdown.Split('\n').Count(l => l.StartsWith("| **FAKE — pipeline demonstration, not a model result**", StringComparison.Ordinal) && l.Contains("fake-")));
        Assert.True(json.GetProperty("notYetRunAgainstRealModels").GetBoolean());

        // The recommendation is generated and is the oracle: the noisy model is cheaper but fails the gate.
        Assert.Equal("fake-oracle@v1", json.GetProperty("recommendation").GetProperty("chosen").GetString());
        Assert.True(json.GetProperty("recommendation").GetProperty("demonstration").GetBoolean());
        Assert.Contains("not eligible", markdown);

        var rows = json.GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("label").GetString()!);
        var oracle = rows["fake-oracle@v1"];
        var noisy = rows["fake-noisy@v1"];
        Assert.True(oracle.GetProperty("gatesPassed").GetBoolean());
        Assert.False(noisy.GetProperty("gatesPassed").GetBoolean());

        // Token accounting and cost maths: the cost is exactly tokens times the fake price (2.00 in, 8.00 out per 1M).
        var usage = oracle.GetProperty("usage");
        var expected = ((usage.GetProperty("inputTokens").GetInt64() * 2.00m) + (usage.GetProperty("outputTokens").GetInt64() * 8.00m)) / 1_000_000m / 40m; // 40 test-split cases
        Assert.True(usage.GetProperty("inputTokens").GetInt64() > 0);
        Assert.Equal((double)expected, usage.GetProperty("costPerClaimGbp").GetDouble(), 9);
        Assert.Equal(usage.GetProperty("costPerClaimGbp").GetDouble() * 1000, usage.GetProperty("costPer1000ClaimsGbp").GetDouble(), 6);

        // The noisy fake's scripted faults are all visible as their own metrics.
        var noisyUsage = noisy.GetProperty("usage");
        Assert.True(noisyUsage.GetProperty("malformedOutputs").GetInt32() > 0);
        Assert.True(noisyUsage.GetProperty("schemaViolations").GetInt32() > 0);
        Assert.True(noisyUsage.GetProperty("rateLimitRetries").GetInt32() > 0);
        Assert.True(noisyUsage.GetProperty("contentFilterRefusals").GetInt32() > 0);
        Assert.True(noisyUsage.GetProperty("guardRemovals").GetInt32() > 0); // Invented citations removed by the guard.
        Assert.True(noisyUsage.GetProperty("malformedOutputRate").GetDouble() > 0.05);

        // The oracle is perfect on fields, so the extraction path, scorer and cost path all agree.
        Assert.Equal(1.0, oracle.GetProperty("metrics").GetProperty("field_f1_overall").GetDouble(), 6);
        Assert.Equal(0, oracle.GetProperty("metrics").GetProperty("pipeline_errors").GetDouble());
    }

    [Fact]
    public async Task The_prompt_version_and_hash_are_recorded_in_each_runs_results_json()
    {
        var (exit, _, error) = await Run("compare", "--split", "dev", "--dev-fakes", "--models", "fake-oracle", "--prompts", "v1,v2", "--run-id", "prompts", "--out", _out);

        Assert.True(exit == 0, error);
        foreach (var version in new[] { "v1", "v2" })
        {
            var results = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_out, "prompts", $"fake-oracle@{version}", "results.json"))).RootElement;
            var prompts = results.GetProperty("summary").GetProperty("run").GetProperty("prompts").EnumerateArray().ToList();
            Assert.Equal(2, prompts.Count);
            Assert.All(prompts, p => Assert.Equal(version, p.GetProperty("version").GetString()));
            Assert.Equal(Pipeline.Prompts.PromptLibrary.Get("extraction", version).Sha256, prompts[0].GetProperty("sha256").GetString());
        }
    }

    [Fact]
    public async Task Compare_with_only_the_committed_registry_shows_the_baseline_and_three_not_yet_run_rows_and_no_recommendation()
    {
        var (exit, _, error) = await Run("compare", "--split", "test", "--run-id", "real", "--out", _out);

        Assert.True(exit == 0, error);
        var markdown = await File.ReadAllTextAsync(Path.Combine(_out, "real", "comparison.md"));
        Assert.Contains("Not yet run against real models", markdown);
        Assert.DoesNotContain("FAKE", markdown.Split("## Decision rules")[0]);
        Assert.Equal(3, markdown.Split('\n').Count(l => l.Contains("| not yet run against real models", StringComparison.Ordinal)));
        Assert.Contains("**No recommendation.**", markdown);
    }

    [Fact]
    public async Task A_fake_model_needs_the_dev_flag()
    {
        var (exit, _, error) = await Run("--extractor", "llm", "--assessor", "agent", "--model", "fake-oracle", "--out", _out);

        Assert.Equal(2, exit);
        Assert.Contains("--dev-fakes", error);
    }

    [Fact]
    public async Task A_model_flag_without_a_model_or_for_a_disabled_model_is_refused_with_a_message()
    {
        var (noModel, _, e1) = await Run("--extractor", "llm", "--out", _out);
        var (disabled, _, e2) = await Run("--extractor", "llm", "--assessor", "agent", "--model", "gpt-mid", "--out", _out);
        var (heuristicWithModel, _, e3) = await Run("--model", "gpt-mid", "--out", _out);

        Assert.Equal(2, noModel);
        Assert.Contains("--model", e1);
        Assert.Equal(2, disabled);
        Assert.Contains("not enabled", e2);
        Assert.Equal(2, heuristicWithModel);
        Assert.Contains("only apply", e3);
    }

    [Fact]
    public async Task A_single_run_with_the_oracle_writes_the_model_section_and_the_fake_banner()
    {
        var (exit, output, error) = await Run("--extractor", "llm", "--assessor", "agent", "--model", "fake-oracle", "--dev-fakes", "--prompt", "v2", "--split", "dev", "--run-id", "one", "--out", _out);

        Assert.True(exit == 0, error);
        Assert.Contains("FAKE model", output);
        var report = await File.ReadAllTextAsync(Path.Combine(_out, "one", "report.md"));
        Assert.Contains("FAKE — pipeline demonstration, not a model result", report);
        Assert.Contains("SHA-256", report);
        Assert.Contains("## Model run", report);
    }
}
