using System.Globalization;
using ClaimsIntake.Domain;
using ClaimsIntake.Evals;
using ClaimsIntake.Pipeline.Fields;
using ClaimsIntake.Pipeline.Text;

namespace ClaimsIntake.Evals.Tests;

public class GateTests
{
    private static readonly IReadOnlyDictionary<string, double?> Measured =
        new Dictionary<string, double?> { ["f1"] = 0.90, ["latency"] = 120, ["missing"] = null };

    private static Thresholds T(params (string Name, Bound Bound)[] items) =>
        new(null, null, items.ToDictionary(i => i.Name, i => i.Bound));

    [Fact]
    public void A_metric_at_or_above_its_minimum_passes_and_the_boundary_counts_as_passing()
    {
        Assert.True(Gate.Evaluate(T(("f1", new Bound(Min: 0.90))), Measured).Passed);
        Assert.True(Gate.Evaluate(T(("f1", new Bound(Min: 0.85))), Measured).Passed);
    }

    [Fact]
    public void A_metric_below_its_minimum_fails_with_a_reason()
    {
        var result = Gate.Evaluate(T(("f1", new Bound(Min: 0.95))), Measured);

        var failure = Assert.Single(result.Failures);
        Assert.False(result.Passed);
        Assert.Contains("below the minimum", failure.Reason);
        Assert.Equal(0.90, failure.Actual);
    }

    [Fact]
    public void A_maximum_is_enforced()
    {
        Assert.True(Gate.Evaluate(T(("latency", new Bound(Max: 120))), Measured).Passed);
        Assert.False(Gate.Evaluate(T(("latency", new Bound(Max: 100))), Measured).Passed);
    }

    [Fact]
    public void A_metric_that_was_not_measured_or_does_not_exist_fails_so_a_typo_cannot_switch_the_gate_off()
    {
        Assert.Equal("not measured", Assert.Single(Gate.Evaluate(T(("missing", new Bound(Min: 0))), Measured).Failures).Reason);
        Assert.Equal("unknown metric", Assert.Single(Gate.Evaluate(T(("f_1", new Bound(Min: 0))), Measured).Failures).Reason);
    }

    [Fact]
    public void One_failure_fails_the_whole_gate()
    {
        var result = Gate.Evaluate(T(("f1", new Bound(Min: 0.5)), ("latency", new Bound(Max: 1))), Measured);

        Assert.False(result.Passed);
        Assert.Equal(2, result.Checks.Count);
        Assert.Single(result.Failures);
    }

    [Fact]
    public void An_empty_threshold_set_passes() => Assert.True(Gate.Evaluate(T(), Measured).Passed);

    [Fact]
    public void Thresholds_are_read_from_json_with_comments()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{ "split": "test", /* note */ "metrics": { "f1": { "min": 0.9 }, "latency": { "max": 5 } } }""");

            var thresholds = Thresholds.Load(path);

            Assert.Equal(0.9, thresholds.Metrics["f1"].Min);
            Assert.Equal(5, thresholds.Metrics["latency"].Max);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_committed_thresholds_only_name_metrics_the_run_produces()
    {
        var thresholds = Thresholds.Load(Path.Combine(Repo.Dataset, "..", "thresholds.json"));
        var summary = Summariser.Summarise("r", "test", "heuristic", "rules", []);

        foreach (var name in thresholds.Metrics.Keys.Where(k => !k.StartsWith("latency_p95_ms.", StringComparison.Ordinal)))
        {
            Assert.True(summary.Metrics.ContainsKey(name), $"thresholds.json names an unknown metric: {name}");
        }
    }
}

public class CliTests : IDisposable
{
    private readonly string _out = Path.Combine(Path.GetTempPath(), "evals-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_out))
        {
            Directory.Delete(_out, true);
        }
    }

    private string Thresholds(string metricsJson)
    {
        Directory.CreateDirectory(_out);
        var path = Path.Combine(_out, "t.json");
        File.WriteAllText(path, "{ \"metrics\": " + metricsJson + " }");
        return path;
    }

    private async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await EvalCli.RunAsync(args, o, e);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public async Task A_run_that_meets_its_thresholds_exits_zero_and_writes_the_report_and_results()
    {
        var t = Thresholds("{ \"route_accuracy\": { \"min\": 0.1 }, \"citation_exists\": { \"min\": 1.0 } }");

        var (code, text, _) = await Run("--split", "dev", "--out", _out, "--run-id", "ok", "--thresholds", t);

        Assert.Equal(0, code);
        Assert.Contains("gate: PASSED", text);
        Assert.Contains("## Gate: PASSED", File.ReadAllText(Path.Combine(_out, "ok", "report.md")));
        Assert.Contains("\"summary\"", File.ReadAllText(Path.Combine(_out, "ok", "results.json")));
    }

    [Fact]
    public async Task A_threshold_the_run_cannot_meet_exits_non_zero_and_says_which()
    {
        var t = Thresholds("{ \"route_accuracy\": { \"min\": 1.01 } }");

        var (code, text, _) = await Run("--split", "dev", "--out", _out, "--run-id", "bad", "--thresholds", t);

        Assert.Equal(1, code);
        Assert.Contains("FAIL route_accuracy", text);
        Assert.Contains("## Gate: FAILED", File.ReadAllText(Path.Combine(_out, "bad", "report.md")));
    }

    [Fact]
    public async Task Bad_arguments_exit_with_two()
    {
        Assert.Equal(2, (await Run("--split", "nonsense")).Code);
        Assert.Equal(2, (await Run("--nope", "x")).Code);
        Assert.Equal(2, (await Run("--extractor", "foundry", "--out", _out)).Code);
        Assert.Equal(2, (await Run("--run-id", "../escape", "--out", _out)).Code);
    }

    [Fact]
    public async Task The_dev_report_lists_misses_and_the_test_report_does_not()
    {
        await Run("--split", "dev", "--out", _out, "--run-id", "d");
        await Run("--split", "test", "--out", _out, "--run-id", "t");

        Assert.Contains("## Misses (dev split)", File.ReadAllText(Path.Combine(_out, "d", "report.md")));
        Assert.Contains("held out", File.ReadAllText(Path.Combine(_out, "t", "report.md")));
    }

    [Fact]
    public async Task The_result_does_not_depend_on_the_day_it_is_run()
    {
        // Every case carries its own received time and the pipeline's clock is fixed to it, so two runs agree exactly.
        await Run("--split", "all", "--out", _out, "--run-id", "one");
        await Run("--split", "all", "--out", _out, "--run-id", "two");

        string Metrics(string id)
        {
            var json = File.ReadAllText(Path.Combine(_out, id, "results.json"));
            return string.Join('\n', json.Split('\n').Where(l => !l.Contains("Ms\"", StringComparison.Ordinal) && !l.Contains("runId", StringComparison.Ordinal)))
                .Split("\"latency\"")[0];
        }

        Assert.Equal(Metrics("one"), Metrics("two"));
    }
}

public class DatasetTests
{
    private static readonly IReadOnlyList<EvalCase> Cases = DatasetLoader.Load(Repo.Dataset);

    [Fact]
    public void The_dataset_is_about_fifty_cases_split_into_roughly_fifteen_dev_and_thirty_five_test()
    {
        Assert.InRange(Cases.Count, 45, 60);
        Assert.InRange(Cases.Count(c => c.Split == Splits.Dev), 12, 18);
        Assert.InRange(Cases.Count(c => c.Split == Splits.Test), 30, 42);
    }

    [Fact]
    public void Every_case_has_a_document_a_unique_id_and_tags()
    {
        Assert.Equal(Cases.Count, Cases.Select(c => c.Id).Distinct().Count());
        Assert.All(Cases, c =>
        {
            Assert.NotEmpty(c.Content);
            Assert.NotEmpty(c.Tags);
            Assert.StartsWith(c.Id, c.File, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void The_dataset_covers_every_kind_of_difficulty_the_brief_asks_for()
    {
        var tags = Cases.SelectMany(c => c.Tags).ToHashSet();

        foreach (var tag in new[]
                 {
                     "clean", "pdf", "email", "eml", "missing_field", "amount_words", "uk_dates", "policy_typo", "garbled", "ocr_noise", "multiple_dates",
                     "exclusion", "late_notification", "duplicate_claim", "duplicate_file", "non_claim", "quality_gate", "stated_vs_received",
                 })
        {
            Assert.Contains(tag, tags);
        }
    }

    [Fact]
    public void Both_splits_contain_a_range_of_tags_so_dev_is_a_fair_guide()
    {
        var dev = Cases.Where(c => c.Split == Splits.Dev).SelectMany(c => c.Tags).ToHashSet();

        Assert.True(dev.Count >= 12, $"dev covers only {dev.Count} tags");
    }

    [Fact]
    public void Expected_clauses_exist_in_the_wording_of_the_policy_and_in_the_right_section()
    {
        var policies = new ClaimsIntake.Legacy.InMemoryPolicyStore();
        var wordings = Repo.Wordings.ToDictionary(w => w.Id);

        foreach (var c in Cases.Where(c => c.Expected.Findings.Count > 0))
        {
            var policy = policies.Find(c.Expected.Fields.PolicyNumber!);
            Assert.True(policy is not null, $"{c.Id}: findings expected on an unknown policy");
            var clauses = wordings[policy!.WordingId].Clauses.ToDictionary(x => x.ClauseRef);
            foreach (var f in c.Expected.Findings.Where(f => f.Kind != "PossibleDuplicate"))
            {
                Assert.True(clauses.TryGetValue(f.ClauseRef, out var clause), $"{c.Id}: clause {f.ClauseRef} not in {policy.WordingId}");
                var section = f.Kind switch { "CoverageClause" => "Insured perils", "PossibleExclusion" => "Exclusions", _ => "Conditions" };
                Assert.Equal(section, clause!.Section);
            }
        }
    }

    [Fact]
    public void The_expected_route_agrees_with_the_expected_findings()
    {
        foreach (var c in Cases)
        {
            var senior = c.Expected.Findings.Any(f => f.Kind is "PossibleExclusion" or "NotificationBreach" or "PossibleDuplicate")
                || c.Expected.Issue_Quality();
            if (senior)
            {
                Assert.Equal("SeniorReview", c.Expected.Route);
            }
        }
    }

    [Fact]
    public void Expected_late_notifications_match_the_dates_and_the_wording_period()
    {
        var policies = new ClaimsIntake.Legacy.InMemoryPolicyStore();
        var wordings = Repo.Wordings.ToDictionary(w => w.Id);
        var within = new System.Text.RegularExpressions.Regex(@"within\s+(\d+)\s+days");

        foreach (var c in Cases.Where(c => c.Expected.Fields.PolicyNumber is not null && c.Expected.Fields.LossDate is not null && c.Expected.Fields.NotificationDate is not null))
        {
            var policy = policies.Find(c.Expected.Fields.PolicyNumber!);
            if (policy is null)
            {
                continue;
            }

            var notification = wordings[policy.WordingId].Clauses.Single(x => x.ClauseRef == "5.1");
            var days = int.Parse(within.Match(notification.Text).Groups[1].Value, CultureInfo.InvariantCulture);
            var elapsed = DateOnly.Parse(c.Expected.Fields.NotificationDate!, CultureInfo.InvariantCulture).DayNumber
                - DateOnly.Parse(c.Expected.Fields.LossDate!, CultureInfo.InvariantCulture).DayNumber;

            Assert.Equal(elapsed > days, c.Expected.Findings.Any(f => f.Kind == "NotificationBreach"));
        }
    }

    [Fact]
    public void Expected_policy_period_issues_match_the_seeded_policies()
    {
        var policies = new ClaimsIntake.Legacy.InMemoryPolicyStore();

        foreach (var c in Cases.Where(c => c.Expected.Fields.PolicyNumber is not null && c.Expected.Fields.LossDate is not null))
        {
            var policy = policies.Find(c.Expected.Fields.PolicyNumber!);
            if (policy is null)
            {
                Assert.Contains("UnknownPolicy", c.Expected.IssueCodes);
                continue;
            }

            var loss = DateOnly.Parse(c.Expected.Fields.LossDate!, CultureInfo.InvariantCulture);
            Assert.Equal(loss < policy.PeriodStart || loss > policy.PeriodEnd, c.Expected.IssueCodes.Contains("LossDateOutsidePolicyPeriod"));
        }
    }

    [Fact]
    public async Task A_stated_notification_date_appears_in_the_document_text()
    {
        foreach (var c in Cases.Where(c => c.Expected.NotificationSource == "document" && c.File.EndsWith(".pdf", StringComparison.Ordinal)))
        {
            var text = await new LocalPdfTextExtractor().ExtractAsync(new IntakeDocument("x", c.File, BinaryData.FromBytes(c.Content)), CancellationToken.None);
            Assert.NotNull(NotificationDateReader.Read(text));
            Assert.Equal(DateOnly.Parse(c.Expected.Fields.NotificationDate!, CultureInfo.InvariantCulture), NotificationDateReader.Read(text));
        }
    }

    [Fact]
    public void The_ground_truth_is_not_derived_from_the_pipeline_it_scores()
    {
        // The generator never imports or runs the pipeline; this guards the claim in evals/README.md.
        var generator = File.ReadAllText(Path.Combine(Repo.Dataset, "..", "generate_dataset.py"));

        Assert.DoesNotContain("dotnet", generator, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class ExpectedExtensions
{
    /// <summary>True for a document the input-quality gate should stop (its only expected issue is an input-quality code).</summary>
    public static bool Issue_Quality(this Expected e) =>
        e.IssueCodes.Any(c => c is "UnreadableDocument" or "NotAClaim" or "DocumentTooLarge" or "ProtectedOrCorruptDocument" or "DuplicateDocument");
}

public class PlumbingTests
{
    /// <summary>
    /// Stands in for a model that answers perfectly. It proves the model path of the harness end to end (an extractor other
    /// than the heuristic, run through the real pipeline, scored by the real scorer) without any credentials. It says nothing
    /// about how a real model would score.
    /// </summary>
    private sealed class OracleExtractor(IReadOnlyDictionary<string, EvalCase> byFile) : IClaimExtractor
    {
        private static readonly Dictionary<string, string> Descriptions = new()
        {
            ["fire"] = "A fire damaged the premises.", ["water"] = "A pipe burst and water damaged stock.", ["theft"] = "Thieves stole stock.",
            ["storm"] = "Storm damage to the roof.", ["impact"] = "A vehicle collided with the door.", ["flood"] = "Surface water flooded the unit.",
            ["refrigeration"] = "The freezer failed.", ["breakdown"] = "The machine seized.",
        };

        public Task<IReadOnlyList<ExtractedField>> ExtractAsync(string documentId, DocumentText text, CancellationToken cancellationToken)
        {
            var f = byFile[documentId["inbox/".Length..]].Expected.Fields;
            ExtractedField Make(string name, string? value) => new(name, value, value is null ? 0 : 0.99);
            IReadOnlyList<ExtractedField> fields =
            [
                Make(ClaimFields.PolicyNumber, f.PolicyNumber), Make(ClaimFields.ClaimantName, f.ClaimantName), Make(ClaimFields.LossDate, f.LossDate),
                Make(ClaimFields.LossDescription, f.Cause is null ? null : Descriptions[f.Cause]),
                Make(ClaimFields.EstimatedAmount, f.EstimatedAmountGbp is { } a ? "£" + a.ToString("0.00", CultureInfo.InvariantCulture) : null),
            ];
            return Task.FromResult(fields);
        }
    }

    [Fact]
    public async Task A_perfect_extractor_scores_perfectly_on_every_field_so_the_harness_and_scorer_are_sound()
    {
        var all = DatasetLoader.Load(Repo.Dataset);
        var config = new PipelineConfig("oracle", new OracleExtractor(all.ToDictionary(c => c.File)), "rules", () => new ClaimsIntake.Pipeline.Assessment.RuleBasedClaimAssessor());

        var (summary, _) = await EvalCli.EvaluateAsync(config, all, all, Repo.Wordings, "plumbing", Splits.All, CancellationToken.None);

        foreach (var field in FieldNames.All)
        {
            Assert.Equal(1.0, summary.Fields[field].F1);
        }

        Assert.Equal(0, summary.PipelineErrors);
        Assert.Equal(1.0, summary.Groundedness.TextMatchShare);
        Assert.Equal(1.0, summary.Issues.Precision);
    }

    [Fact]
    public void The_offline_implementations_need_no_model_and_the_model_ones_refuse_to_be_built_without_one()
    {
        Assert.Equal("heuristic", PipelineConfigs.Create("Heuristic", "Rules").ExtractorName);
        Assert.Throws<NotSupportedException>(() => PipelineConfigs.Create("foundry", "rules")); // Not a name we know.
        Assert.Throws<NotSupportedException>(() => PipelineConfigs.Create("heuristic", "swarm"));
        Assert.Throws<ArgumentException>(() => PipelineConfigs.Create("llm", "rules"));
        Assert.Throws<ArgumentException>(() => PipelineConfigs.Create("heuristic", "agent"));
    }
}
