using System.Net;
using ClaimsIntake.Evals;
using ClaimsIntake.Pipeline;
using ClaimsIntake.Pipeline.Fields;
using ClaimsIntake.Pipeline.Prompts;
using ClaimsIntake.Pipeline.Text;
using Microsoft.Extensions.AI;

namespace ClaimsIntake.Evals.Tests;

/// <summary>A chat client that plays a script: each call runs the next step, which returns a response or throws. The last step repeats.</summary>
internal sealed class ScriptedChatClient(params Func<IReadOnlyList<ChatMessage>, ChatResponse>[] steps) : IChatClient
{
    private int _next;

    public int Calls => Volatile.Read(ref _next);

    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        Requests.Add(list);
        var step = steps[Math.Min(Interlocked.Increment(ref _next) - 1, steps.Length - 1)];
        return Task.FromResult(step(list));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    public static Func<IReadOnlyList<ChatMessage>, ChatResponse> Reply(string text, long? input = 100, long? output = 20) =>
        _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            Usage = input is null ? null : new UsageDetails { InputTokenCount = input, OutputTokenCount = output },
        };

    public static Func<IReadOnlyList<ChatMessage>, ChatResponse> Throw(Exception ex) => _ => throw ex;

    public static Func<IReadOnlyList<ChatMessage>, ChatResponse> RateLimited() =>
        Throw(new HttpRequestException("429 Too Many Requests", null, HttpStatusCode.TooManyRequests));
}

public class TokenAccountingAndCostTests
{
    private static readonly ModelPrice Price = new("GBP", 2.00m, 8.00m, "https://example.test/price", "2026-11-14", "verify-before-use");

    [Fact]
    public void Cost_is_tokens_times_price_per_million()
    {
        Assert.Equal(2.00m, Price.Cost(1_000_000, 0));
        Assert.Equal(8.00m, Price.Cost(0, 1_000_000));
        // 1,500 in and 500 out: 0.003 + 0.004.
        Assert.Equal(0.007m, Price.Cost(1_500, 500));
        Assert.Equal(0m, Price.Cost(0, 0));
    }

    [Fact]
    public async Task Each_call_records_its_tokens_and_latency_against_the_current_claim()
    {
        var inner = new ScriptedChatClient(ScriptedChatClient.Reply("a", 100, 20), ScriptedChatClient.Reply("b", 250, 30));
        var client = new MeteringChatClient(inner, new CallPolicy());
        var usage = new CaseUsage();
        CaseUsage.Current = usage;
        try
        {
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")]);
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "y")]);
        }
        finally
        {
            CaseUsage.Current = null;
        }

        var record = usage.Snapshot(null);
        Assert.Equal(2, record.Calls);
        Assert.Equal(350, record.InputTokens);
        Assert.Equal(50, record.OutputTokens);
        Assert.Equal(2, record.CallMs.Count);
        Assert.False(record.UsageMissing);
    }

    [Fact]
    public async Task A_model_that_reports_no_usage_is_flagged_and_its_cost_is_not_computed()
    {
        var client = new MeteringChatClient(new ScriptedChatClient(ScriptedChatClient.Reply("a", input: null)), new CallPolicy());
        var usage = new CaseUsage();
        CaseUsage.Current = usage;
        try
        {
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")]);
        }
        finally
        {
            CaseUsage.Current = null;
        }

        var score = Scores.Score(usage.Snapshot(null));
        var summary = Summariser.Summarise("r", "test", "llm", "agent", [score], Scores.Info(Price));
        Assert.True(summary.Model!.UsageMissing);
        Assert.Null(summary.Model.CostPerClaimGbp);
        Assert.Null(summary.Metrics["cost_per_claim_gbp"]); // Not measured, so a gate on it fails rather than passes.
    }

    [Fact]
    public void Summary_cost_per_claim_is_the_summed_cost_over_the_claims_and_per_thousand_is_a_thousand_times_that()
    {
        // Claim 1: 1,000,000 in, 0 out = £2.00. Claim 2: 0 in, 500,000 out = £4.00. Mean £3.00.
        var scores = new[]
        {
            Scores.Score(new UsageRecord(2, 1_000_000, 0, [10, 20], 0, 0, 0, false, 0, 0, 0)),
            Scores.Score(new UsageRecord(1, 0, 500_000, [30], 0, 0, 0, false, 0, 0, 0)),
        };

        var summary = Summariser.Summarise("r", "test", "llm", "agent", scores, Scores.Info(Price));

        Assert.Equal(3.00, summary.Model!.CostPerClaimGbp!.Value, 6);
        Assert.Equal(3000.00, summary.Model.CostPer1000ClaimsGbp!.Value, 4);
        Assert.Equal(3.00, summary.Metrics["cost_per_claim_gbp"]!.Value, 6);
        Assert.Equal(3, summary.Model.Calls);
        Assert.Equal(20, summary.Model.CallP50Ms!.Value, 6); // Median of 10, 20 and 30.
        Assert.False(summary.Model.PriceVerified);
    }

    [Fact]
    public void The_offline_baseline_costs_nothing_and_has_no_model_section()
    {
        var summary = Summariser.Summarise("r", "test", "heuristic", "rules", [Scores.Score(UsageRecord.None)]);

        Assert.Null(summary.Model);
        Assert.Equal(0.0, summary.Metrics["cost_per_claim_gbp"]);
        Assert.DoesNotContain("malformed_output_rate", summary.Metrics.Keys);
    }
}

public class RateLimitRetryTests
{
    private static (CallPolicy Policy, List<TimeSpan> Waits) Policy(int maxRetries = 5) =>
        Create(maxRetries);

    private static (CallPolicy, List<TimeSpan>) Create(int maxRetries)
    {
        var waits = new List<TimeSpan>();
        var policy = new CallPolicy(2, maxRetries, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), (d, _) =>
        {
            waits.Add(d);
            return Task.CompletedTask;
        });
        return (policy, waits);
    }

    [Fact]
    public async Task A_429_is_retried_with_growing_back_off_and_then_succeeds()
    {
        var (policy, waits) = Policy();
        var inner = new ScriptedChatClient(ScriptedChatClient.RateLimited(), ScriptedChatClient.RateLimited(), ScriptedChatClient.Reply("ok", 10, 5));
        var usage = new CaseUsage();
        CaseUsage.Current = usage;
        ChatResponse response;
        try
        {
            response = await new MeteringChatClient(inner, policy).GetResponseAsync([new ChatMessage(ChatRole.User, "x")]);
        }
        finally
        {
            CaseUsage.Current = null;
        }

        Assert.Equal("ok", response.Text);
        Assert.Equal(3, inner.Calls);
        var record = usage.Snapshot(null);
        Assert.Equal(2, record.RateLimitRetries);
        Assert.Equal(1, record.Calls); // Only the successful call counts as a call with tokens.
        Assert.Equal(2, waits.Count);
        Assert.InRange(waits[0].TotalSeconds, 1.0, 1.25); // 1 s plus up to a quarter of jitter.
        Assert.InRange(waits[1].TotalSeconds, 2.0, 2.5); // Doubled.
    }

    [Fact]
    public async Task Back_off_is_capped()
    {
        var (policy, _) = Policy();

        Assert.InRange(policy.BackoffFor(20).TotalSeconds, 30.0, 37.5);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task When_the_retries_run_out_the_429_is_rethrown_and_counted_as_a_failed_call()
    {
        var (policy, waits) = Policy(maxRetries: 2);
        var inner = new ScriptedChatClient(ScriptedChatClient.RateLimited());
        var usage = new CaseUsage();
        CaseUsage.Current = usage;
        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => new MeteringChatClient(inner, policy).GetResponseAsync([new ChatMessage(ChatRole.User, "x")]));
        }
        finally
        {
            CaseUsage.Current = null;
        }

        Assert.Equal(3, inner.Calls); // The first try and two retries.
        Assert.Equal(2, waits.Count);
        var record = usage.Snapshot(null);
        Assert.Equal(2, record.RateLimitRetries);
        Assert.Equal(1, record.FailedCalls);
    }

    [Fact]
    public async Task Other_errors_are_not_retried()
    {
        var (policy, waits) = Policy();
        var inner = new ScriptedChatClient(ScriptedChatClient.Throw(new HttpRequestException("boom", null, HttpStatusCode.InternalServerError)));

        await Assert.ThrowsAsync<HttpRequestException>(() => new MeteringChatClient(inner, policy).GetResponseAsync([new ChatMessage(ChatRole.User, "x")]));

        Assert.Equal(1, inner.Calls);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task A_content_filter_refusal_is_counted_and_not_retried()
    {
        var (policy, waits) = Policy();
        var inner = new ScriptedChatClient(ScriptedChatClient.Throw(new InvalidOperationException("HTTP 400: content_filter triggered")));
        var usage = new CaseUsage();
        CaseUsage.Current = usage;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new MeteringChatClient(inner, policy).GetResponseAsync([new ChatMessage(ChatRole.User, "x")]));
        }
        finally
        {
            CaseUsage.Current = null;
        }

        Assert.Equal(1, inner.Calls);
        Assert.Empty(waits);
        Assert.Equal(1, usage.Snapshot(null).ContentFilterRefusals);
    }

    [Fact]
    public async Task A_content_filter_finish_reason_is_counted()
    {
        var (policy, _) = Policy();
        var inner = new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "")) { FinishReason = ChatFinishReason.ContentFilter });
        var usage = new CaseUsage();
        CaseUsage.Current = usage;
        try
        {
            await new MeteringChatClient(inner, policy).GetResponseAsync([new ChatMessage(ChatRole.User, "x")]);
        }
        finally
        {
            CaseUsage.Current = null;
        }

        Assert.Equal(1, usage.Snapshot(null).ContentFilterRefusals);
    }

    [Theory]
    [InlineData(429, true)]
    [InlineData(500, false)]
    public void Status_codes_are_read_from_exceptions_that_expose_one(int status, bool limited) =>
        Assert.Equal(limited, CallPolicy.IsRateLimited(new StatusException(status)));

    [Fact]
    public void A_429_wrapped_in_another_exception_is_still_found() =>
        Assert.True(CallPolicy.IsRateLimited(new InvalidOperationException("wrapper", new StatusException(429))));

    [Fact]
    public async Task Parallel_calls_never_exceed_the_bound()
    {
        var policy = new CallPolicy(maxConcurrentCalls: 3);
        var inFlight = 0;
        var peak = 0;
        var inner = new ScriptedChatClient(_ =>
        {
            var now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref peak, now);
            Thread.Sleep(15);
            Interlocked.Decrement(ref inFlight);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        });
        var client = new MeteringChatClient(inner, policy);

        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")]))));

        Assert.True(peak <= 3, $"peak was {peak}");
        Assert.Equal(12, inner.Calls);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            Interlocked.CompareExchange(ref target, value, current);
        }
    }

    private sealed class StatusException(int status) : Exception("status")
    {
        public int Status { get; } = status;
    }
}

public class MalformedOutputTests
{
    private static readonly DocumentText Doc = new(["Policy number: TM-CP-100001\nLoss date: 3 November 2026"]);

    private static async Task<(ModelRunMetrics Metrics, Exception? Error, IReadOnlyList<ExtractedFieldView> Fields)> Extract(string reply)
    {
        var metrics = new ModelRunMetrics();
        ModelRunMetrics.Current = metrics;
        try
        {
            var extractor = new FoundryLlmClaimExtractor(new ScriptedChatClient(ScriptedChatClient.Reply(reply)));
            try
            {
                var fields = await extractor.ExtractAsync("doc", Doc, CancellationToken.None);
                return (metrics, null, [.. fields.Select(f => new ExtractedFieldView(f.Name, f.Value))]);
            }
            catch (ClaimExtractionException ex)
            {
                return (metrics, ex, []);
            }
        }
        finally
        {
            ModelRunMetrics.Current = null;
        }
    }

    private sealed record ExtractedFieldView(string Name, string? Value);

    [Fact]
    public async Task A_reply_that_is_not_json_is_counted_as_malformed_and_fails_the_document()
    {
        var (metrics, error, _) = await Extract("Sure! Here are the fields: {policy_number: oops");

        Assert.NotNull(error);
        Assert.Equal(1, metrics.MalformedOutputs);
        Assert.Equal(0, metrics.SchemaViolations);
    }

    [Fact]
    public async Task An_empty_reply_is_counted_as_malformed()
    {
        var (metrics, error, _) = await Extract("   ");

        Assert.NotNull(error);
        Assert.Equal(1, metrics.MalformedOutputs);
    }

    [Fact]
    public async Task Json_of_the_wrong_shape_is_counted_once_as_a_schema_violation_and_the_missing_fields_are_not_found()
    {
        var (metrics, error, fields) = await Extract("""{"policy_number":{"value":"TM-CP-100001","quote":"TM-CP-100001","confidence":0.9}}""");

        Assert.Null(error);
        Assert.Equal(0, metrics.MalformedOutputs);
        Assert.Equal(1, metrics.SchemaViolations); // One reply, however many fields are missing.
        Assert.Equal("TM-CP-100001", fields.Single(f => f.Name == "policy_number").Value);
        Assert.Null(fields.Single(f => f.Name == "loss_date").Value);
    }

    [Fact]
    public async Task A_well_formed_reply_counts_nothing()
    {
        var reply = """
            {"policy_number":{"value":"TM-CP-100001","quote":"TM-CP-100001","confidence":0.95},
             "claimant_name":{"value":null,"quote":null,"confidence":0},
             "loss_date":{"value":"3 November 2026","quote":"3 November 2026","confidence":0.9},
             "loss_description":{"value":null,"quote":null,"confidence":0},
             "estimated_amount_gbp":{"value":null,"quote":null,"confidence":0}}
            """;

        var (metrics, error, _) = await Extract(reply);

        Assert.Null(error);
        Assert.Equal((0, 0, 0), (metrics.MalformedOutputs, metrics.SchemaViolations, metrics.GuardRemovals));
    }

    [Fact]
    public void Nothing_is_counted_when_no_run_is_in_progress()
    {
        ModelRunMetrics.Malformed(); // Must not throw: production code calls this with no collector set.
        ModelRunMetrics.GuardRemoved(3);

        Assert.Null(ModelRunMetrics.Current);
    }

    [Fact]
    public void The_malformed_rate_is_malformed_plus_schema_violations_over_model_calls()
    {
        var price = new ModelPrice("GBP", 1m, 1m, "x", "2026-11-14", "verify-before-use");
        var scores = new[] { Scores.Score(new UsageRecord(4, 400, 40, [1, 2, 3, 4], 0, 0, 0, false, 1, 1, 2)), Scores.Score(new UsageRecord(4, 400, 40, [1, 2, 3, 4], 0, 0, 0, false, 0, 0, 0)) };

        var summary = Summariser.Summarise("r", "test", "llm", "agent", scores, Scores.Info(price));

        Assert.Equal(2 / 8.0, summary.Model!.MalformedOutputRate!.Value, 6);
        Assert.Equal(2, summary.Model.GuardRemovals);
        Assert.Equal(0.25, summary.Metrics["malformed_output_rate"]!.Value, 6);
    }
}

public class PromptVersionTests
{
    [Fact]
    public void Versions_v1_and_v2_exist_for_both_stages()
    {
        Assert.Equal(["v1", "v2"], PromptLibrary.Versions(PromptLibrary.Extraction));
        Assert.Equal(["v1", "v2"], PromptLibrary.Versions(PromptLibrary.Assessment));
    }

    [Fact]
    public void The_hash_is_the_sha256_of_the_normalised_text_and_differs_between_versions()
    {
        var v1 = PromptLibrary.Get(PromptLibrary.Extraction, "v1");
        var v2 = PromptLibrary.Get(PromptLibrary.Extraction, "v2");

        Assert.Equal(64, v1.Sha256.Length);
        Assert.Matches("^[0-9a-f]{64}$", v1.Sha256);
        Assert.NotEqual(v1.Sha256, v2.Sha256);
        Assert.Equal(v1.Sha256, PromptVersion.Create("extraction", "v1", v1.Text).Sha256); // Stable: hashing the text again gives the same hash.
        Assert.Equal(v1.Sha256[..12], v1.ShortHash);
    }

    [Fact]
    public void Line_endings_and_trailing_whitespace_do_not_change_the_hash()
    {
        var lf = PromptVersion.Create("extraction", "t", "line one\nline two\n");
        var crlf = PromptVersion.Create("extraction", "t", "line one\r\nline two\r\n\r\n");
        var changed = PromptVersion.Create("extraction", "t", "line one\nline TWO\n");

        Assert.Equal(lf.Sha256, crlf.Sha256);
        Assert.NotEqual(lf.Sha256, changed.Sha256);
    }

    [Fact]
    public void The_default_version_is_v1_and_an_unknown_version_names_the_choices()
    {
        Assert.Equal("v1", PromptLibrary.Get(PromptLibrary.Assessment).Version);

        var ex = Assert.Throws<ArgumentException>(() => PromptLibrary.Get(PromptLibrary.Extraction, "v9"));
        Assert.Contains("v1, v2", ex.Message);
    }

    [Fact]
    public void V1_is_the_prompt_that_shipped_before_versioning()
    {
        Assert.StartsWith("You extract fields from UK commercial insurance claim documents (emails and forms).", PromptLibrary.Get(PromptLibrary.Extraction, "v1").Text);
        Assert.EndsWith("Dates and amounts stay as written.", PromptLibrary.Get(PromptLibrary.Extraction, "v1").Text);
        Assert.Contains("Never cite a clause from memory", PromptLibrary.Get(PromptLibrary.Assessment, "v1").Text);
    }

    [Fact]
    public void V2_addresses_the_known_weaknesses()
    {
        var text = PromptLibrary.Get(PromptLibrary.Extraction, "v2").Text;

        Assert.Contains("two-digit year", text);
        Assert.Contains("burgled", text);
        Assert.Contains("letter O", text);
        Assert.Contains("email", text);
    }

    [Fact]
    public async Task The_extractor_sends_the_prompt_version_it_was_given()
    {
        var client = new ScriptedChatClient(ScriptedChatClient.Reply("not json"));
        var v2 = PromptLibrary.Get(PromptLibrary.Extraction, "v2");

        await Assert.ThrowsAsync<ClaimExtractionException>(() => new FoundryLlmClaimExtractor(client, v2).ExtractAsync("d", new DocumentText(["x"]), CancellationToken.None));

        Assert.Equal(v2.Text, client.Requests[0].Single(m => m.Role == ChatRole.System).Text);
    }

    [Fact]
    public void The_prompt_versions_and_hashes_are_recorded_in_the_run_info()
    {
        var model = ModelRegistry.Fakes[0];
        var selection = new ModelSelection(model, "v2", new CallPolicy(), _ => new ScriptedChatClient(ScriptedChatClient.Reply("{}")));

        var config = PipelineConfigs.Create("llm", "agent", selection, Repo.Wordings);

        Assert.Equal(["extraction", "assessment"], config.Info.Prompts.Select(p => p.Kind));
        Assert.All(config.Info.Prompts, p => Assert.Equal("v2", p.Version));
        Assert.Equal(PromptLibrary.Get(PromptLibrary.Extraction, "v2").Sha256, config.Info.Prompts[0].Sha256);
        Assert.Equal(PromptLibrary.Get(PromptLibrary.Assessment, "v2").Sha256, config.Info.Prompts[1].Sha256);
    }
}

internal static class Scores
{
    public static RunInfo Info(ModelPrice price) => new("t@v1", "t", "azure-openai", false, [], price);

    public static CaseScore Score(UsageRecord usage) =>
        new("c", [], "test", new Dictionary<string, FieldOutcome>(), new Dictionary<string, string?>(), [], [], false, [], [], [],
            "SeniorReview", "SeniorReview", false, false, false, false, false, false, [], null, usage);
}
