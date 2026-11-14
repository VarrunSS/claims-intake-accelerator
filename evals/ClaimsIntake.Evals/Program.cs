using System.Globalization;
using ClaimsIntake.Evals;
using ClaimsIntake.Pipeline.Policy;
using ClaimsIntake.Pipeline.Prompts;

return await EvalCli.RunAsync(args, Console.Out, Console.Error);

namespace ClaimsIntake.Evals
{
    /// <summary>
    /// The command line. Exit codes: 0 the run finished and every threshold held; 1 a threshold was missed;
    /// 2 the run could not be done (bad arguments, missing dataset, a model that is not deployed).
    /// </summary>
    public static class EvalCli
    {
        public const string Usage = """
            Usage: ClaimsIntake.Evals [run] [options]      score one configuration (the default command)
                   ClaimsIntake.Evals compare [options]    run several model x prompt configurations over one split

            Common options:
              --split dev|test|all       Which cases to run (default: test).
              --out <folder>             Where results/<run-id>/ is written (default: evals/results).
              --run-id <id>              Name of the results folder (default: <UTC time>-<split>).
              --dataset <folder>         The dataset (default: evals/dataset, found above the current folder).
              --wordings <folder>        The policy wordings (default: the copy next to the program).
              --registry <file>          The model registry (default: evals/models.json).
              --parallelism <n>          Claims in flight at once, and the cap on model calls in flight (default: 1 for the
                                         offline baseline, 4 for a model).
              --dev-fakes                DEV ONLY. Allows the fake models fake-oracle and fake-noisy, which know the answers.
                                         Their results are a pipeline demonstration, never a model result.

            run options:
              --thresholds <file>        Fail (exit 1) if a metric is outside its bound. Optional.
              --extractor heuristic|llm  Default heuristic (offline). llm needs --model.
              --assessor rules|agent     Default rules (offline). agent needs --model.
              --model <id>               A model id from the registry. Authenticates with DefaultAzureCredential.
              --prompt <version>         Prompt version for the model-backed stages (default v1). See prompts/.

            compare options:
              --models <id,id,...>       Models to compare (default: every model in the registry). A model that is not
                                         enabled in the registry is listed as "not yet run".
              --prompts <v1,v2,...>      Prompt versions to try with each model (default: v1).
              --thresholds <file>        The gate for the recommendation (default: evals/model-thresholds.json).
            """;

        private static readonly string[] Flags = ["help", "dev-fakes"];

        private static readonly string[] Known =
            ["split", "out", "run-id", "thresholds", "dataset", "wordings", "extractor", "assessor", "model", "prompt", "registry", "parallelism", "models", "prompts", "help", "dev-fakes"];

        public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
        {
            var command = "run";
            if (args.Length > 0 && args[0] is "run" or "compare")
            {
                command = args[0];
                args = args[1..];
            }

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
                var evalsFolder = Path.GetDirectoryName(Path.GetFullPath(dataset))!;
                var runId = options.GetValueOrDefault("run-id") ?? $"{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{split}";
                if (runId.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')) || runId.StartsWith('.'))
                {
                    throw new ArgumentException("--run-id may contain only letters, digits, '-', '_' and '.'.");
                }

                var outFolder = options.GetValueOrDefault("out", Path.Combine(evalsFolder, "results"));
                return command == "compare"
                    ? await CompareAsync(options, output, cases, allCases, wordings, evalsFolder, outFolder, runId, split)
                    : await RunOneAsync(options, output, cases, allCases, wordings, evalsFolder, outFolder, runId, split);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or NotSupportedException or InvalidOperationException
                or System.Text.Json.JsonException or UriFormatException)
            {
                await error.WriteLineAsync($"error: {ex.Message}");
                return 2;
            }
        }

        private static async Task<int> RunOneAsync(
            Dictionary<string, string> options, TextWriter output, IReadOnlyList<EvalCase> cases, IReadOnlyList<EvalCase> allCases,
            IReadOnlyList<PolicyWording> wordings, string evalsFolder, string outFolder, string runId, string split)
        {
            var extractor = options.GetValueOrDefault("extractor", "heuristic");
            var assessor = options.GetValueOrDefault("assessor", "rules");
            var usesModel = extractor.Equals("llm", StringComparison.OrdinalIgnoreCase) || assessor.Equals("agent", StringComparison.OrdinalIgnoreCase);
            var parallelism = ParseParallelism(options, usesModel ? 4 : 1);
            ModelSelection? selection = null;
            if (usesModel)
            {
                if (!options.TryGetValue("model", out var modelId))
                {
                    throw new ArgumentException("--extractor llm and --assessor agent need --model <id> (see evals/models.json).");
                }

                var registry = ModelRegistry.Load(options.GetValueOrDefault("registry") ?? Path.Combine(evalsFolder, "models.json"));
                var entry = registry.Find(modelId, options.ContainsKey("dev-fakes"));
                selection = new ModelSelection(entry, options.GetValueOrDefault("prompt", PromptLibrary.DefaultVersion), PolicyFor(entry, parallelism));
            }
            else if (options.ContainsKey("model") || options.ContainsKey("prompt"))
            {
                throw new ArgumentException("--model and --prompt only apply with --extractor llm or --assessor agent.");
            }

            var config = PipelineConfigs.Create(extractor, assessor, selection, wordings, parallelism: parallelism);
            var thresholds = options.TryGetValue("thresholds", out var thresholdPath) ? Thresholds.Load(thresholdPath) : null;

            var (summary, scores) = await EvaluateAsync(config, cases, allCases, wordings, runId, split, CancellationToken.None);
            var gate = thresholds is null ? null : Gate.Evaluate(thresholds, summary.Metrics);
            var folder = await WriteRunAsync(Path.Combine(outFolder, runId), summary, scores, gate, split);

            await output.WriteAsync(Report.ToSummaryText(summary, gate));
            await output.WriteLineAsync($"  written: {Path.Combine(folder, "report.md")}");
            return gate is { Passed: false } ? 1 : 0;
        }

        private static async Task<int> CompareAsync(
            Dictionary<string, string> options, TextWriter output, IReadOnlyList<EvalCase> cases, IReadOnlyList<EvalCase> allCases,
            IReadOnlyList<PolicyWording> wordings, string evalsFolder, string outFolder, string runId, string split)
        {
            var devFakes = options.ContainsKey("dev-fakes");
            var parallelism = ParseParallelism(options, 4);
            var registryPath = options.GetValueOrDefault("registry") ?? Path.Combine(evalsFolder, "models.json");
            var registry = File.Exists(registryPath) || !options.ContainsKey("models") ? ModelRegistry.Load(registryPath) : new ModelRegistry([]);
            var gatePath = options.GetValueOrDefault("thresholds") ?? Path.Combine(evalsFolder, "model-thresholds.json");
            var gates = Thresholds.Load(gatePath);
            var ids = options.TryGetValue("models", out var list)
                ? [.. list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
                : registry.Models.Select(m => m.Id).ToList();
            var prompts = options.GetValueOrDefault("prompts", PromptLibrary.DefaultVersion)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var version in prompts)
            {
                _ = PromptLibrary.Get(PromptLibrary.Extraction, version); // Fails early on an unknown version.
                _ = PromptLibrary.Get(PromptLibrary.Assessment, version);
            }

            var root = Path.Combine(outFolder, runId);
            var rows = new List<CompareRow>();

            // The baseline always runs for real: it is the floor, and it shows the table works with no credentials.
            var baseline = await RunRowAsync(PipelineConfigs.Create("heuristic", "rules", parallelism: 1), cases, allCases, wordings, "baseline", split, root, null);
            rows.Add(new CompareRow("baseline", RowKind.Baseline, "heuristic + rules", "none", baseline, null, null, false));

            foreach (var id in ids)
            {
                var entry = registry.Find(id, devFakes);
                if (!entry.Enabled)
                {
                    rows.Add(new CompareRow(entry.Id, RowKind.NotRun, entry.DisplayName, string.Join(", ", prompts), null, null, "not deployed; enabled is false in models.json", false));
                    continue;
                }

                foreach (var version in prompts)
                {
                    var label = $"{entry.Id}@{version}";
                    try
                    {
                        var selection = new ModelSelection(entry, version, PolicyFor(entry, parallelism));
                        var config = PipelineConfigs.Create("llm", "agent", selection, wordings, label, parallelism);
                        var summary = await RunRowAsync(config, cases, allCases, wordings, label, split, root, gates);
                        rows.Add(new CompareRow(label, entry.IsFake ? RowKind.Fake : RowKind.Model, entry.DisplayName, version, summary,
                            Gate.Evaluate(gates, summary.Metrics), null, !entry.IsFake && !entry.Price.IsVerified));
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or UriFormatException or NotSupportedException)
                    {
                        rows.Add(new CompareRow(label, RowKind.NotRun, entry.DisplayName, version, null, null, $"could not start ({ex.GetType().Name})", false));
                    }
                }
            }

            var recommendation = Recommender.Recommend(rows);
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "comparison.md"), ComparisonReport.ToMarkdown(runId, split, DateTimeOffset.UtcNow, rows, recommendation, gates, parallelism));
            await File.WriteAllTextAsync(Path.Combine(root, "comparison.json"), ComparisonReport.ToJson(runId, split, rows, recommendation));
            await output.WriteLineAsync($"compare: {rows.Count} rows, split {split}");
            foreach (var r in rows)
            {
                await output.WriteLineAsync($"  {r.Label,-28} {(r.Summary is null ? "not yet run against real models" : $"field F1 {r.Summary.FieldsOverall.F1:0.000}, gates {(r.Gate is null ? "reference" : r.Gate.Passed ? "pass" : "FAIL")}")}");
            }

            await output.WriteLineAsync(recommendation.Chosen is null ? "  recommendation: none" : $"  recommendation: {recommendation.Chosen}{(recommendation.Demonstration ? " (FAKE demonstration)" : "")}");
            await output.WriteLineAsync($"  written: {Path.Combine(root, "comparison.md")}");
            return 0;
        }

        private static async Task<EvalSummary> RunRowAsync(
            PipelineConfig config, IReadOnlyList<EvalCase> cases, IReadOnlyList<EvalCase> allCases, IReadOnlyList<PolicyWording> wordings,
            string label, string split, string root, Thresholds? gates)
        {
            var (summary, scores) = await EvaluateAsync(config, cases, allCases, wordings, label, split, CancellationToken.None);
            var gate = gates is null ? null : Gate.Evaluate(gates, summary.Metrics);
            await WriteRunAsync(Path.Combine(root, label), summary, scores, gate, split);
            return summary;
        }

        private static async Task<string> WriteRunAsync(string folder, EvalSummary summary, IReadOnlyList<CaseScore> scores, GateResult? gate, string split)
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "report.md"), Report.ToMarkdown(summary, scores, gate, showMisses: split == Splits.Dev));
            await File.WriteAllTextAsync(Path.Combine(folder, "results.json"), Report.ToJson(summary, scores, gate));
            return folder;
        }

        private static CallPolicy PolicyFor(ModelEntry entry, int parallelism) =>
            new(parallelism, baseDelay: entry.IsFake ? TimeSpan.FromMilliseconds(5) : null); // The fakes retry quickly: there is nobody to be polite to.

        private static int ParseParallelism(Dictionary<string, string> options, int fallback) =>
            !options.TryGetValue("parallelism", out var text)
                ? fallback
                : int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 32
                    ? n
                    : throw new ArgumentException("--parallelism must be a whole number from 1 to 32.");

        /// <summary>
        /// Runs and scores the cases, with up to <see cref="PipelineConfig.Parallelism"/> in flight. For the offline baseline one
        /// case is run first and discarded so JIT time does not land in the first latency sample; a model run skips that, because
        /// the discarded claim would cost real tokens.
        /// </summary>
        public static async Task<(EvalSummary Summary, IReadOnlyList<CaseScore> Scores)> EvaluateAsync(
            PipelineConfig config, IReadOnlyList<EvalCase> cases, IReadOnlyList<EvalCase> allCases, IReadOnlyList<PolicyWording> wordings,
            string runId, string split, CancellationToken cancellationToken)
        {
            var byId = allCases.ToDictionary(c => c.Id, StringComparer.Ordinal);
            using var harness = new Harness(config, wordings);
            if (!config.Info.IsModelRun)
            {
                await harness.RunAsync(cases[0], byId, cancellationToken);
            }

            var scores = new CaseScore[cases.Count];
            await Parallel.ForEachAsync(
                Enumerable.Range(0, cases.Count),
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, config.Parallelism), CancellationToken = cancellationToken },
                async (index, token) =>
                {
                    var run = await harness.RunAsync(cases[index], byId, token);
                    scores[index] = CaseScorer.Score(run, wordings);
                });

            return (Summariser.Summarise(runId, split, config.ExtractorName, config.AssessorName, scores, config.Info.IsModelRun ? config.Info : null), scores);
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

                if (arg.StartsWith("--", StringComparison.Ordinal) && Flags.Contains(arg[2..]))
                {
                    options[arg[2..]] = "true";
                    continue;
                }

                if (!arg.StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                {
                    throw new ArgumentException($"Unexpected argument \"{arg}\".");
                }

                options[arg[2..]] = args[++i];
            }

            return options.Keys.FirstOrDefault(k => !Known.Contains(k)) is { } unknown
                ? throw new ArgumentException($"Unknown option --{unknown}.")
                : options;
        }
    }
}
