using System.Globalization;
using ClaimsIntake.Evals;
using ClaimsIntake.Pipeline.Policy;

return await EvalCli.RunAsync(args, Console.Out, Console.Error);

namespace ClaimsIntake.Evals
{
    /// <summary>
    /// The command line. Exit codes: 0 the run finished and every threshold held; 1 a threshold was missed;
    /// 2 the run could not be done (bad arguments, missing dataset).
    /// </summary>
    public static class EvalCli
    {
        public const string Usage = """
            Usage: ClaimsIntake.Evals [options]
              --split dev|test|all       Which cases to run (default: test).
              --out <folder>             Where results/<run-id>/ is written (default: evals/results).
              --run-id <id>              Name of the results folder (default: <UTC time>-<split>).
              --thresholds <file>        Fail (exit 1) if a metric is below its threshold. Optional.
              --dataset <folder>         The dataset (default: evals/dataset, found above the current folder).
              --wordings <folder>        The policy wordings (default: the copy next to the program).
              --extractor heuristic      Only the offline extractor runs here.
              --assessor rules           Only the offline assessor runs here.
            """;

        public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
        {
            Dictionary<string, string> options;
            try
            {
                options = Parse(args);
            }
            catch (ArgumentException ex)
            {
                await error.WriteLineAsync(ex.Message);
                await error.WriteLineAsync(Usage);
                return 2;
            }

            if (options.ContainsKey("help"))
            {
                await output.WriteLineAsync(Usage);
                return 0;
            }

            try
            {
                var split = options.GetValueOrDefault("split", Splits.Test);
                if (!Splits.IsValid(split))
                {
                    throw new ArgumentException($"--split must be dev, test or all, not \"{split}\".");
                }

                var dataset = options.GetValueOrDefault("dataset") ?? DatasetLoader.FindDefaultFolder(Directory.GetCurrentDirectory());
                var allCases = DatasetLoader.Load(dataset);
                var cases = split == Splits.All ? allCases : [.. allCases.Where(c => c.Split == split)];
                if (cases.Count == 0)
                {
                    throw new InvalidDataException($"The dataset has no cases in the {split} split.");
                }

                var wordings = PolicyWordingParser.LoadFolder(options.GetValueOrDefault("wordings") ?? Path.Combine(AppContext.BaseDirectory, "policy-wordings"));
                var config = PipelineConfigs.Create(options.GetValueOrDefault("extractor", "heuristic"), options.GetValueOrDefault("assessor", "rules"));
                var thresholds = options.TryGetValue("thresholds", out var thresholdPath) ? Thresholds.Load(thresholdPath) : null;
                var runId = options.GetValueOrDefault("run-id") ?? $"{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{split}";
                if (runId.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')) || runId.StartsWith('.'))
                {
                    throw new ArgumentException("--run-id may contain only letters, digits, '-', '_' and '.'.");
                }

                var (summary, scores) = await EvaluateAsync(config, cases, allCases, wordings, runId, split, CancellationToken.None);
                var gate = thresholds is null ? null : Gate.Evaluate(thresholds, summary.Metrics);

                var folder = Path.Combine(options.GetValueOrDefault("out", Path.Combine(Path.GetDirectoryName(dataset)!, "results")), runId);
                Directory.CreateDirectory(folder);
                await File.WriteAllTextAsync(Path.Combine(folder, "report.md"), Report.ToMarkdown(summary, scores, gate, showMisses: split == Splits.Dev));
                await File.WriteAllTextAsync(Path.Combine(folder, "results.json"), Report.ToJson(summary, scores, gate));

                await output.WriteAsync(Report.ToSummaryText(summary, gate));
                await output.WriteLineAsync($"  written: {Path.Combine(folder, "report.md")}");
                return gate is { Passed: false } ? 1 : 0;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or NotSupportedException or System.Text.Json.JsonException)
            {
                await error.WriteLineAsync($"error: {ex.Message}");
                return 2;
            }
        }

        /// <summary>Runs and scores the cases. One case is run first and discarded so JIT time does not land in the first latency sample.</summary>
        public static async Task<(EvalSummary Summary, IReadOnlyList<CaseScore> Scores)> EvaluateAsync(
            PipelineConfig config, IReadOnlyList<EvalCase> cases, IReadOnlyList<EvalCase> allCases, IReadOnlyList<PolicyWording> wordings,
            string runId, string split, CancellationToken cancellationToken)
        {
            var byId = allCases.ToDictionary(c => c.Id, StringComparer.Ordinal);
            using var harness = new Harness(config, wordings);
            await harness.RunAsync(cases[0], byId, cancellationToken);

            var scores = new List<CaseScore>();
            foreach (var item in cases)
            {
                var run = await harness.RunAsync(item, byId, cancellationToken);
                scores.Add(CaseScorer.Score(run, wordings));
            }

            return (Summariser.Summarise(runId, split, config.ExtractorName, config.AssessorName, scores), scores);
        }

        private static Dictionary<string, string> Parse(string[] args)
        {
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg is "--help" or "-h")
                {
                    options["help"] = "true";
                    continue;
                }

                if (!arg.StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                {
                    throw new ArgumentException($"Unexpected argument \"{arg}\".");
                }

                options[arg[2..]] = args[++i];
            }

            var known = new[] { "split", "out", "run-id", "thresholds", "dataset", "wordings", "extractor", "assessor", "help" };
            return options.Keys.FirstOrDefault(k => !known.Contains(k)) is { } unknown
                ? throw new ArgumentException($"Unknown option --{unknown}.")
                : options;
        }
    }
}
