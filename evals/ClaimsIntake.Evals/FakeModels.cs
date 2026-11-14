using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ClaimsIntake.Domain;
using ClaimsIntake.Legacy;
using ClaimsIntake.Pipeline.Policy;
using ClaimsIntake.Pipeline.Tools;
using Microsoft.Extensions.AI;

namespace ClaimsIntake.Evals;

// FAKE MODELS: everything in this file exists to demonstrate the comparison pipeline with no credentials. These clients
// know the ground truth of the case being run. They are not models, their answers say nothing about any real model, and
// anything they produce must be labelled "FAKE: pipeline demonstration, not a model result". They are reachable from the
// command line only with --dev-fakes.

/// <summary>The case the fake models are answering. Set by the harness around each case; fakes refuse to run without it.</summary>
public static class FakeModelContext
{
    private static readonly AsyncLocal<EvalCase?> CurrentValue = new();

    public static EvalCase? Current
    {
        get => CurrentValue.Value;
        set => CurrentValue.Value = value;
    }
}

/// <summary>How a fake behaves: the oracle returns the ground truth, the noisy one adds scripted, repeatable faults.</summary>
public enum FakeProfile
{
    Oracle,
    Noisy,
}

/// <summary>A deterministic stand-in for dice: the same case and salt always give the same number in [0, 1).</summary>
internal static class Dice
{
    public static double Roll(string caseId, string salt)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{caseId}|{salt}"));
        return BitConverter.ToUInt32(hash, 0) / (uint.MaxValue + 1.0);
    }
}

public static class FakeModels
{
    public static FakeProfile ProfileOf(ModelEntry model) =>
        model.Id.Equals(ModelRegistry.FakeNoisy, StringComparison.OrdinalIgnoreCase) ? FakeProfile.Noisy : FakeProfile.Oracle;

    public static IChatClient CreateExtraction(ModelEntry model) => new FakeExtractionClient(ProfileOf(model));

    public static IChatClient CreateAssessment(ModelEntry model, IReadOnlyList<PolicyWording> wordings) => new FakeAssessmentClient(ProfileOf(model), wordings);

    /// <summary>A rough token count (four characters to a token), so the fakes have usage to account for.</summary>
    internal static long Tokens(string text) => (text.Length + 3) / 4;

    internal static long Tokens(IEnumerable<ChatMessage> messages) =>
        messages.Sum(m => Tokens(m.Text + string.Concat(m.Contents.OfType<FunctionResultContent>().Select(r => r.Result?.ToString() ?? ""))
            + string.Concat(m.Contents.OfType<FunctionCallContent>().Select(c => c.Name))));

    internal static EvalCase RequireCase() =>
        FakeModelContext.Current ?? throw new InvalidOperationException("A fake model was called outside the evaluation harness, so it has no case to answer.");

    /// <summary>The words a loss description would use for a cause, which the shared cause classifier recognises.</summary>
    internal static string DescribeCause(string cause) => cause switch
    {
        "impact" => "struck by a vehicle",
        "refrigeration" => "freezer failure",
        _ => cause,
    };
}

internal abstract class FakeClientBase(FakeProfile profile) : IChatClient
{
    private readonly ConcurrentDictionary<string, bool> _rateLimited = new(StringComparer.Ordinal);

    protected FakeProfile Profile => profile;

    protected bool Noisy => profile == FakeProfile.Noisy;

    public abstract Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    /// <summary>The noisy fake rate-limits some cases once (so the retry path runs) and refuses a few under a content filter.</summary>
    protected void InjectServiceFaults(EvalCase item, string stage)
    {
        if (!Noisy)
        {
            return;
        }

        if (Dice.Roll(item.Id, stage + ":429") < 0.15 && _rateLimited.TryAdd($"{item.Id}:{stage}", true))
        {
            throw new HttpRequestException("FAKE 429 Too Many Requests", null, HttpStatusCode.TooManyRequests);
        }

        if (Dice.Roll(item.Id, stage + ":filter") < 0.04)
        {
            throw new InvalidOperationException("FAKE content_filter: the prompt was refused by the content filter");
        }
    }

    protected static ChatResponse Reply(string text, long inputTokens) =>
        new(new ChatMessage(ChatRole.Assistant, text)) { Usage = new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = FakeModels.Tokens(text), TotalTokenCount = inputTokens + FakeModels.Tokens(text) } };
}

/// <summary>FAKE. Answers the extraction prompt with the case's ground-truth fields (oracle) or with those plus faults (noisy).</summary>
internal sealed class FakeExtractionClient(FakeProfile profile) : FakeClientBase(profile)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var item = FakeModels.RequireCase();
        var list = messages.ToList();
        var input = FakeModels.Tokens(list);
        var document = DecodeDocument(list.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "");
        InjectServiceFaults(item, "extract");

        if (Noisy)
        {
            var roll = Dice.Roll(item.Id, "extract:shape");
            if (roll < 0.10)
            {
                return Task.FromResult(Reply("Sure! Here are the fields: {policy_number: not sure", input)); // Not JSON.
            }

            if (roll < 0.18)
            {
                return Task.FromResult(Reply("""{"policy_number":{"value":"TM-CP-100001","quote":null,"confidence":0.9}}""", input)); // JSON, wrong shape.
            }
        }

        var f = item.Expected.Fields;
        var amount = f.EstimatedAmountGbp?.ToString("0.00", CultureInfo.InvariantCulture);
        var lossDate = f.LossDate;
        if (Noisy && Dice.Roll(item.Id, "extract:amount") < 0.20)
        {
            amount = null;
        }

        if (Noisy && Dice.Roll(item.Id, "extract:date") < 0.20 && DateOnly.TryParseExact(lossDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            lossDate = d.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        var json = new JsonObject
        {
            [ClaimFields.PolicyNumber] = Field(f.PolicyNumber, document),
            [ClaimFields.ClaimantName] = Field(f.ClaimantName, document),
            [ClaimFields.LossDate] = Field(lossDate, document),
            [ClaimFields.LossDescription] = Field(f.Cause is null ? null : FakeModels.DescribeCause(f.Cause), document),
            [ClaimFields.EstimatedAmount] = Field(amount, document),
        };
        return Task.FromResult(Reply(json.ToJsonString(), input));
    }

    /// <summary>
    /// The extraction prompt carries the document as JSON string data (AI-03). An honest model reads the decoded text, so the fake
    /// does too: the pages are joined with a blank line, as the text the pipeline located the fields in.
    /// </summary>
    private static string DecodeDocument(string userMessage)
    {
        var start = userMessage.IndexOf('{', StringComparison.Ordinal);
        var end = userMessage.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            return userMessage;
        }

        try
        {
            using var parsed = System.Text.Json.JsonDocument.Parse(userMessage[start..(end + 1)]);
            return string.Join("\n", parsed.RootElement.GetProperty("pages").EnumerateArray().Select(p => p.GetProperty("text").GetString() ?? ""));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return userMessage;
        }
    }

    // The fake has no reading to do, so its quote is the value if the document has it verbatim, otherwise the first real line of
    // the document. Either way the extractor can place the field in the text, as it could for an honest model.
    private static JsonObject Field(string? value, string document) => new()
    {
        ["value"] = value,
        ["quote"] = value is null ? null : document.Contains(value, StringComparison.Ordinal) ? value : FirstLine(document),
        ["confidence"] = value is null ? 0.0 : 0.95,
    };

    private static string? FirstLine(string document) =>
        document.Split("\n").Select(l => l.Trim()).FirstOrDefault(l => l.Length > 3 && !l.StartsWith('<') && !l.StartsWith("[page", StringComparison.Ordinal)) is { } line
            ? (line.Length > 60 ? line[..60] : line)
            : null;
}

/// <summary>
/// FAKE. Plays the assessment agent: one turn of tool calls (policy, one search per clause the ground truth cites, duplicate
/// check), then the ground-truth answer (oracle) or that plus a made-up citation or unreadable reply (noisy).
/// </summary>
internal sealed class FakeAssessmentClient(FakeProfile profile, IReadOnlyList<PolicyWording> wordings) : FakeClientBase(profile)
{
    private readonly InMemoryPolicyStore _policies = new();

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var item = FakeModels.RequireCase();
        var list = messages.ToList();
        var input = FakeModels.Tokens(list);
        var toolsAlreadyRan = list.Any(m => m.Contents.OfType<FunctionResultContent>().Any());
        var wordingId = _policies.Find(item.Expected.Fields.PolicyNumber ?? "")?.WordingId;

        if (!toolsAlreadyRan)
        {
            InjectServiceFaults(item, "assess");
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ToolCalls(item, wordingId)))
            {
                Usage = new UsageDetails { InputTokenCount = input, OutputTokenCount = 60, TotalTokenCount = input + 60 },
            });
        }

        if (Noisy && Dice.Roll(item.Id, "assess:shape") < 0.12)
        {
            return Task.FromResult(Reply("I have reviewed the claim and it looks fine to me.", input));
        }

        var findings = new JsonArray();
        foreach (var finding in item.Expected.Findings)
        {
            if (Noisy && finding.Kind == "CoverageClause" && Dice.Roll(item.Id, "assess:skip") < 0.10)
            {
                continue; // The noisy model sometimes forgets the insured-peril finding.
            }

            findings.Add(new JsonObject
            {
                ["kind"] = finding.Kind,
                ["summary"] = $"FAKE finding: {finding.Kind}.",
                ["citations"] = finding.ClauseRef.Length == 0 ? new JsonArray() : new JsonArray(new JsonObject { ["wordingId"] = wordingId, ["clauseRef"] = finding.ClauseRef }),
                ["relatedClaimRef"] = finding.Kind == "PossibleDuplicate" ? item.Context.ExistingClaims.FirstOrDefault()?.Reference : null,
            });
        }

        if (Noisy && Dice.Roll(item.Id, "assess:invent") < 0.30)
        {
            findings.Add(new JsonObject
            {
                ["kind"] = "PossibleExclusion",
                ["summary"] = "FAKE invented exclusion.",
                ["citations"] = new JsonArray(new JsonObject { ["wordingId"] = wordingId ?? "TM-PW-CC-2026", ["clauseRef"] = "99.9" }),
            });
        }

        var answer = new JsonObject
        {
            ["findings"] = findings,
            ["route"] = item.Expected.Route == "StraightToHandler" ? "straight_to_handler" : "senior_review",
            ["routeReason"] = "FAKE: taken from the ground truth.",
        };
        return Task.FromResult(Reply(answer.ToJsonString(), input));
    }

    private List<AIContent> ToolCalls(EvalCase item, string? wordingId)
    {
        var calls = new List<AIContent>();
        var policy = item.Expected.Fields.PolicyNumber;
        if (policy is null)
        {
            return [new TextContent("{\"findings\":[],\"route\":\"senior_review\",\"routeReason\":\"FAKE: no policy number.\"}")];
        }

        calls.Add(new FunctionCallContent("call-lookup", ToolNames.LookupPolicy, new Dictionary<string, object?> { ["policyNumber"] = policy }));
        var index = 0;
        foreach (var finding in item.Expected.Findings.Where(f => f.ClauseRef.Length > 0))
        {
            var clause = wordings.Where(w => w.Id == wordingId).SelectMany(w => w.Clauses).FirstOrDefault(c => c.ClauseRef == finding.ClauseRef);
            if (clause is null)
            {
                continue;
            }

            var query = $"{clause.Heading} {(clause.Text.Length > 160 ? clause.Text[..160] : clause.Text)}";
            calls.Add(new FunctionCallContent($"call-search-{index++}", ToolNames.SearchPolicyWording,
                new Dictionary<string, object?> { ["query"] = query, ["wordingId"] = wordingId, ["top"] = 10 }));
        }

        if (item.Expected.Fields.LossDate is { } lossDate)
        {
            calls.Add(new FunctionCallContent("call-duplicate", ToolNames.CheckDuplicate,
                new Dictionary<string, object?> { ["policyNumber"] = policy, ["lossDate"] = lossDate, ["amountGbp"] = item.Expected.Fields.EstimatedAmountGbp }));
        }

        return calls;
    }
}
