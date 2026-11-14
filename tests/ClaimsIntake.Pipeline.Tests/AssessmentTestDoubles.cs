using ClaimsIntake.Domain;
using ClaimsIntake.Pipeline.Assessment;
using ClaimsIntake.Pipeline.Fields;
using ClaimsIntake.Pipeline.Handoff;
using ClaimsIntake.Pipeline.Legacy;
using ClaimsIntake.Pipeline.Policy;
using ClaimsIntake.Pipeline.Text;
using ClaimsIntake.Pipeline.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaimsIntake.Pipeline.Tests;

/// <summary>The real policy wordings, copied next to the test assembly, and a BM25 search over them.</summary>
internal static class Corpus
{
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "policy-wordings");

    public static IReadOnlyList<PolicyWording> Wordings { get; } = PolicyWordingParser.LoadFolder(Folder);

    public static Bm25PolicyWordingSearch Search() => new(Wordings);
}

internal sealed class FakeExistingClaims(params ExistingClaim[] claims) : IExistingClaims
{
    public List<string> Lookups { get; } = [];

    public Task<IReadOnlyList<ExistingClaim>> FindByPolicyAsync(string policyNumber, CancellationToken cancellationToken)
    {
        Lookups.Add(policyNumber);
        return Task.FromResult<IReadOnlyList<ExistingClaim>>(
            [.. claims.Where(c => c.PolicyNumber.Equals(policyNumber, StringComparison.OrdinalIgnoreCase))]);
    }
}

/// <summary>A search that returns what it is given and counts calls, for tests that need to control the corpus.</summary>
internal sealed class CountingSearch(IPolicyWordingSearch inner) : IPolicyWordingSearch
{
    public List<(string Query, string? WordingId, int Top)> Calls { get; } = [];

    public Task<IReadOnlyList<ClauseHit>> SearchAsync(string query, string? wordingId, int top, CancellationToken cancellationToken)
    {
        Calls.Add((query, wordingId, top));
        return inner.SearchAsync(query, wordingId, top, cancellationToken);
    }
}

internal sealed class ThrowingAssessor : IClaimAssessor
{
    public string Name => "rules";

    public Task<ClaimsIntake.Domain.Assessment.ClaimAssessment> AssessAsync(AssessmentContext context, ClaimTools tools, CancellationToken cancellationToken)
    {
        // Makes one call first, so the test can check that the trail so far is kept.
        return Fail();

        async Task<ClaimsIntake.Domain.Assessment.ClaimAssessment> Fail()
        {
            await tools.LookupPolicyAsync("TM-CP-100001", cancellationToken);
            throw new InvalidOperationException("Brindle & Marsh Joinery water damage should never reach a log");
        }
    }
}

internal static class Assessing
{
    public static ClaimTools Tools(
        Guid? claimId = null,
        IPolicyLookup? policies = null,
        IPolicyWordingSearch? search = null,
        IExistingClaims? existing = null) =>
        new(claimId ?? Guid.NewGuid(), policies ?? FakePolicyLookup.Seeded(), search ?? Corpus.Search(),
            existing ?? new FakeExistingClaims(), new FixedTimeProvider(TestTime.Now));

    public static AssessmentRunner Runner(
        IClaimAssessor assessor,
        IPolicyLookup? policies = null,
        IPolicyWordingSearch? search = null,
        IExistingClaims? existing = null,
        ILogger<AssessmentRunner>? logger = null) =>
        new(assessor, policies ?? FakePolicyLookup.Seeded(), search ?? Corpus.Search(), existing ?? new FakeExistingClaims(),
            new FixedTimeProvider(TestTime.Now), logger ?? NullLogger<AssessmentRunner>.Instance);

    /// <summary>The pipeline as Docs.Pipeline builds it, with an assessor on the end.</summary>
    public static ClaimIntakePipeline Pipeline(
        IClaimAssessor? assessor = null,
        IExistingClaims? existing = null,
        IClaimSink? sink = null,
        ILogger<ClaimIntakePipeline>? logger = null,
        ILogger<AssessmentRunner>? runnerLogger = null) =>
        new(new LocalPdfTextExtractor(), new HeuristicClaimExtractor(), FakePolicyLookup.Seeded(), sink ?? new RecordingSink(),
            new FixedTimeProvider(TestTime.Now), logger ?? new ListLogger<ClaimIntakePipeline>(),
            Runner(assessor ?? new RuleBasedClaimAssessor(), existing: existing, logger: runnerLogger));

    public static AssessmentContext Context(
        string policy = "TM-CP-100001", string lossDate = "2026-11-03", string description = "A water pipe burst in the workshop store room.",
        string amount = "£12,500", DateOnly? notifiedOn = null, Guid? claimId = null) =>
        new(claimId ?? Guid.NewGuid(),
            new Dictionary<string, ExtractedField>
            {
                [ClaimFields.PolicyNumber] = new(ClaimFields.PolicyNumber, policy, 0.95),
                [ClaimFields.LossDate] = new(ClaimFields.LossDate, lossDate, 0.95),
                [ClaimFields.LossDescription] = new(ClaimFields.LossDescription, description, 0.9),
                [ClaimFields.EstimatedAmount] = new(ClaimFields.EstimatedAmount, amount, 0.9),
                [ClaimFields.ClaimantName] = new(ClaimFields.ClaimantName, "Brindle & Marsh Joinery Ltd", 0.9),
            },
            notifiedOn ?? new DateOnly(2026, 11, 14));
}

/// <summary>
/// A chat client that plays a script: each call to the model returns the next step. Lets a test make the "model"
/// call tools and then answer, including with a citation it made up.
/// </summary>
internal sealed class ScriptedChatClient(params Func<IReadOnlyList<ChatMessage>, ChatResponse>[] steps) : IChatClient
{
    private int _next;

    public int Calls => _next;
    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];
    public List<ChatOptions?> Options { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        Requests.Add(list);
        Options.Add(options);
        var step = _next < steps.Length ? steps[_next] : steps[^1];
        _next++;
        return Task.FromResult(step(list));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    public static Func<IReadOnlyList<ChatMessage>, ChatResponse> CallTool(string name, Dictionary<string, object?> arguments, string callId = "") =>
        _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId.Length == 0 ? $"call-{name}" : callId, name, arguments)]));

    /// <summary>Several tool calls in one model turn.</summary>
    public static Func<IReadOnlyList<ChatMessage>, ChatResponse> CallTools(params (string Name, Dictionary<string, object?> Arguments)[] calls) =>
        _ => new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [.. calls.Select((c, i) => (AIContent)new FunctionCallContent($"call-{i}-{c.Name}", c.Name, c.Arguments))]));

    public static Func<IReadOnlyList<ChatMessage>, ChatResponse> Answer(string text) =>
        _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
}
