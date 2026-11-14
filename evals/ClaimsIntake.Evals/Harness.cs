using System.Collections.Concurrent;
using System.Diagnostics;
using ClaimsIntake.Domain;
using ClaimsIntake.Domain.Assessment;
using ClaimsIntake.Domain.Intake;
using ClaimsIntake.Legacy;
using ClaimsIntake.Pipeline;
using ClaimsIntake.Pipeline.Assessment;
using ClaimsIntake.Pipeline.Fields;
using ClaimsIntake.Pipeline.Handoff;
using ClaimsIntake.Pipeline.Legacy;
using ClaimsIntake.Pipeline.Policy;
using ClaimsIntake.Pipeline.Prompts;
using ClaimsIntake.Pipeline.Quality;
using ClaimsIntake.Pipeline.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaimsIntake.Evals;

/// <summary>The pieces of the pipeline an evaluation run configures. The defaults are the local, offline ones.</summary>
public sealed record PipelineConfig(string ExtractorName, IClaimExtractor Extractor, string AssessorName, Func<IClaimAssessor> Assessor)
{
    /// <summary>What the run is, for results.json. The offline baseline by default.</summary>
    public RunInfo Info { get; init; } = RunInfo.Baseline;

    /// <summary>How many claims are processed at once. Model calls are separately bounded by the <see cref="CallPolicy"/>.</summary>
    public int Parallelism { get; init; } = 1;
}

/// <summary>The model-backed choices for a run: which model, which prompt version, how calls are spaced.</summary>
/// <param name="ClientFactory">Builds the chat client for an entry. Defaults to the real one; the tests pass scripted clients.</param>
public sealed record ModelSelection(ModelEntry Model, string PromptVersion, CallPolicy Policy, Func<ModelEntry, IChatClient>? ClientFactory = null);

public static class PipelineConfigs
{
    /// <summary>
    /// Builds the configuration for the names the command line accepts: <c>heuristic</c> or <c>llm</c> for the extractor,
    /// <c>rules</c> or <c>agent</c> for the assessor. The model-backed ones need a <see cref="ModelSelection"/>; they are
    /// never built unless asked for, so the offline gate needs no credentials.
    /// </summary>
    public static PipelineConfig Create(
        string extractor, string assessor, ModelSelection? selection = null, IReadOnlyList<PolicyWording>? wordings = null, string? label = null, int parallelism = 1)
    {
        var llm = extractor.Equals("llm", StringComparison.OrdinalIgnoreCase);
        var agent = assessor.Equals("agent", StringComparison.OrdinalIgnoreCase);
        if (!llm && !extractor.Equals("heuristic", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Extractor \"{extractor}\" is unknown. Use \"heuristic\" or \"llm\".");
        }

        if (!agent && !assessor.Equals("rules", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Assessor \"{assessor}\" is unknown. Use \"rules\" or \"agent\".");
        }

        if (!llm && !agent)
        {
            return new PipelineConfig("heuristic", new HeuristicClaimExtractor(), "rules", () => new RuleBasedClaimAssessor()) { Parallelism = parallelism };
        }

        if (selection is null)
        {
            throw new ArgumentException("--extractor llm and --assessor agent need --model <id> (see evals/models.json).");
        }

        var model = selection.Model;
        IChatClient Client(Func<ModelEntry, IChatClient> fake) =>
            new MeteringChatClient(selection.ClientFactory?.Invoke(model) ?? (model.IsFake ? fake(model) : ChatClientFactory.Create(model)), selection.Policy);

        var prompts = new List<PromptInfo>();
        IClaimExtractor claimExtractor = new HeuristicClaimExtractor();
        if (llm)
        {
            var prompt = PromptLibrary.Get(PromptLibrary.Extraction, selection.PromptVersion);
            prompts.Add(new PromptInfo(prompt.Kind, prompt.Version, prompt.Sha256));
            claimExtractor = new FoundryLlmClaimExtractor(Client(FakeModels.CreateExtraction), prompt);
        }

        Func<IClaimAssessor> claimAssessor = () => new RuleBasedClaimAssessor();
        if (agent)
        {
            var prompt = PromptLibrary.Get(PromptLibrary.Assessment, selection.PromptVersion);
            prompts.Add(new PromptInfo(prompt.Kind, prompt.Version, prompt.Sha256));
            var client = Client(m => FakeModels.CreateAssessment(m, wordings ?? throw new ArgumentException("The fake assessor needs the policy wordings.")));
            claimAssessor = () => new AgentClaimAssessor(client, prompt);
        }

        var info = new RunInfo(label ?? $"{model.Id}@{selection.PromptVersion}", model.Id, model.Kind, model.IsFake, prompts, model.Price);
        return new PipelineConfig(llm ? "llm" : "heuristic", claimExtractor, agent ? "agent" : "rules", claimAssessor) { Info = info, Parallelism = parallelism };
    }
}

/// <summary>The time a stage took, in milliseconds, for one case.</summary>
public sealed record StageTiming(string Stage, double Milliseconds);

/// <summary>What the pipeline produced for a case, and how long each stage took. <see cref="Claim"/> is null when the pipeline threw.</summary>
public sealed record CaseRun(EvalCase Case, ProposedClaim? Claim, IReadOnlyList<StageTiming> Timings, string? Error, UsageRecord? Usage = null);

/// <summary>
/// Runs cases through the configured pipeline in process. Each case gets its own clock fixed at the case's received time,
/// its own duplicate-file record and its own existing claims, so a case's result never depends on the day it is run, on
/// the order of the cases, or on any service. Stage timings come from the pipeline's own OpenTelemetry spans.
/// </summary>
public sealed class Harness : IDisposable
{
    private readonly PipelineConfig _config;
    private readonly IPolicyLookup _policies = new SeededPolicyLookup();
    private readonly IPolicyWordingSearch _search;
    private readonly ConcurrentDictionary<string, ConcurrentQueue<StageTiming>> _spans = new(StringComparer.Ordinal);
    private readonly ActivityListener _listener;

    public Harness(PipelineConfig config, IReadOnlyList<PolicyWording> wordings)
    {
        _config = config;
        _search = new Bm25PolicyWordingSearch(wordings);
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PipelineTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.GetTagItem(PipelineTelemetry.Stage) is string stage
                    && activity.GetTagItem(PipelineTelemetry.DocumentId) is string document
                    && activity.GetTagItem(PipelineTelemetry.DurationMs) is double ms)
                {
                    _spans.GetOrAdd(document, _ => new()).Enqueue(new StageTiming(stage, ms));
                }
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public async Task<CaseRun> RunAsync(EvalCase item, IReadOnlyDictionary<string, EvalCase> all, CancellationToken cancellationToken)
    {
        var time = new CaseClock(item.Context.ReceivedAt);
        var documentId = $"inbox/{item.File}";

        var hashes = new InMemoryDocumentHashStore();
        if (item.Context.DuplicateOf is { } otherId)
        {
            var other = all.TryGetValue(otherId, out var o)
                ? o
                : throw new InvalidDataException($"{item.Id}: duplicateOf names an unknown case \"{otherId}\".");
            hashes.RegisterOrFindEarlier(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(other.Content)), $"inbox/{other.File}");
        }

        var options = new InputQualityOptions();
        if (item.Context.MaxDocumentBytes is { } max)
        {
            options.MaxDocumentBytes = max;
        }

        var sink = new CapturingSink();
        var existing = new StaticExistingClaims(item.Context.ExistingClaims);
        var runner = new AssessmentRunner(_config.Assessor(), _policies, _search, existing, time, NullLogger<AssessmentRunner>.Instance);
        var pipeline = new ClaimIntakePipeline(
            new LocalPdfTextExtractor(), _config.Extractor, _policies, sink, time, NullLogger<ClaimIntakePipeline>.Instance, runner,
            InputQualityGate.CreateDefault(options, hashes));

        var document = new IntakeDocument(documentId, item.File, BinaryData.FromBytes(item.Content), item.Context.ReceivedAt);

        // Per-claim scopes: token and retry counts, output-quality counts, and (fake models only) the case being answered.
        var usage = new CaseUsage();
        var outputs = new ModelRunMetrics();
        CaseUsage.Current = usage;
        ModelRunMetrics.Current = outputs;
        FakeModelContext.Current = item;
        string? error = null;
        try
        {
            await pipeline.ProcessAsync(document, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.GetType().Name;
        }
        finally
        {
            CaseUsage.Current = null;
            ModelRunMetrics.Current = null;
            FakeModelContext.Current = null;
        }

        var timings = _spans.TryRemove(ClaimIntakePipeline.Sanitise(documentId), out var queue) ? [.. queue] : new List<StageTiming>();
        return new CaseRun(item, sink.Claim, timings, error, usage.Snapshot(outputs));
    }

    public void Dispose() => _listener.Dispose();

    private sealed class CaseClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CapturingSink : IClaimSink
    {
        public ProposedClaim? Claim { get; private set; }

        public Task HandOffAsync(ProposedClaim claim, CancellationToken cancellationToken)
        {
            Claim = claim;
            return Task.CompletedTask;
        }
    }

    /// <summary>The ten policies the legacy mock seeds, so the evaluation and the running system agree.</summary>
    private sealed class SeededPolicyLookup : IPolicyLookup
    {
        private readonly InMemoryPolicyStore _store = new();

        public Task<PolicyReference?> FindAsync(string policyNumber, CancellationToken cancellationToken)
        {
            var p = _store.Find(policyNumber);
            return Task.FromResult(p is null ? null : new PolicyReference(p.Number, p.InsuredName, p.PeriodStart, p.PeriodEnd, p.WordingId, p.ExcessGbp));
        }
    }

    private sealed class StaticExistingClaims(IReadOnlyList<ExistingClaimSpec> claims) : IExistingClaims
    {
        public Task<IReadOnlyList<ExistingClaim>> FindByPolicyAsync(string policyNumber, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExistingClaim>>(
            [
                .. claims
                    .Where(c => string.Equals(c.PolicyNumber, policyNumber, StringComparison.OrdinalIgnoreCase))
                    .Select(c => new ExistingClaim(c.Reference, c.Source, c.PolicyNumber!,
                        c.LossDate is null ? null : DateOnly.Parse(c.LossDate, System.Globalization.CultureInfo.InvariantCulture),
                        c.AmountGbp, c.Status ?? "Submitted")),
            ]);
    }
}
