using System.Diagnostics;
using System.Net;
using System.Reflection;
using ClaimsIntake.Pipeline;
using Microsoft.Extensions.AI;

namespace ClaimsIntake.Evals;

/// <summary>Per-claim tally of model calls: tokens, time, retries and refusals. Counts and numbers only, never text.</summary>
public sealed class CaseUsage
{
    private static readonly AsyncLocal<CaseUsage?> CurrentValue = new();
    private readonly object _gate = new();
    private readonly List<double> _callMs = [];
    private long _input;
    private long _output;
    private int _calls;
    private int _rateLimitRetries;
    private int _contentFilter;
    private int _failedCalls;
    private bool _usageMissing;

    public static CaseUsage? Current
    {
        get => CurrentValue.Value;
        set => CurrentValue.Value = value;
    }

    public void AddCall(double ms, UsageDetails? usage)
    {
        lock (_gate)
        {
            _calls++;
            _callMs.Add(ms);
            if (usage is null || usage.InputTokenCount is null || usage.OutputTokenCount is null)
            {
                _usageMissing = true; // A model that reports no usage cannot be costed, and the report says so.
            }

            _input += usage?.InputTokenCount ?? 0;
            _output += usage?.OutputTokenCount ?? 0;
        }
    }

    public void AddRateLimitRetry()
    {
        lock (_gate)
        {
            _rateLimitRetries++;
        }
    }

    public void AddContentFilter()
    {
        lock (_gate)
        {
            _contentFilter++;
        }
    }

    public void AddFailedCall()
    {
        lock (_gate)
        {
            _failedCalls++;
        }
    }

    public UsageRecord Snapshot(ModelRunMetrics? outputs)
    {
        lock (_gate)
        {
            return new UsageRecord(_calls, _input, _output, [.. _callMs], _rateLimitRetries, _contentFilter, _failedCalls, _usageMissing,
                outputs?.MalformedOutputs ?? 0, outputs?.SchemaViolations ?? 0, outputs?.GuardRemovals ?? 0);
        }
    }
}

/// <summary>What one claim cost in model calls, as recorded in results.json.</summary>
public sealed record UsageRecord(
    int Calls, long InputTokens, long OutputTokens, IReadOnlyList<double> CallMs, int RateLimitRetries, int ContentFilterRefusals,
    int FailedCalls, bool UsageMissing, int MalformedOutputs, int SchemaViolations, int GuardRemovals)
{
    public static readonly UsageRecord None = new(0, 0, 0, [], 0, 0, 0, false, 0, 0, 0);
}

/// <summary>How the evaluation spaces out its model calls: bounded parallelism and exponential backoff on HTTP 429.</summary>
public sealed class CallPolicy
{
    public CallPolicy(int maxConcurrentCalls = 4, int maxRetries = 5, TimeSpan? baseDelay = null, TimeSpan? maxDelay = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        Gate = new SemaphoreSlim(Math.Max(1, maxConcurrentCalls));
        MaxRetries = maxRetries;
        BaseDelay = baseDelay ?? TimeSpan.FromSeconds(1);
        MaxDelay = maxDelay ?? TimeSpan.FromSeconds(30);
        Delay = delay ?? Task.Delay;
    }

    public SemaphoreSlim Gate { get; }

    public int MaxRetries { get; }

    public TimeSpan BaseDelay { get; }

    public TimeSpan MaxDelay { get; }

    public Func<TimeSpan, CancellationToken, Task> Delay { get; }

    /// <summary>Exponential backoff, doubling each attempt up to the cap, plus up to a quarter again of jitter.</summary>
    public TimeSpan BackoffFor(int attempt)
    {
        var exponential = Math.Min(MaxDelay.TotalMilliseconds, BaseDelay.TotalMilliseconds * Math.Pow(2, attempt));
        return TimeSpan.FromMilliseconds(exponential * (1 + (Random.Shared.NextDouble() * 0.25)));
    }

    /// <summary>True for an HTTP 429 from any of the SDKs: Azure.Core, System.ClientModel, HttpClient, or an exception exposing a status.</summary>
    public static bool IsRateLimited(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } || StatusOf(e) == 429)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the service refused the prompt or the answer under its content filter.</summary>
    public static bool IsContentFilter(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e.Message.Contains("content_filter", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("ResponsibleAIPolicyViolation", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int? StatusOf(Exception e)
    {
        foreach (var name in new[] { "Status", "StatusCode" })
        {
            var value = e.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(e);
            switch (value)
            {
                case int i:
                    return i;
                case HttpStatusCode code:
                    return (int)code;
                case Enum en:
                    return Convert.ToInt32(en, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return null;
    }
}

/// <summary>
/// Wraps a chat client for an evaluation run: it limits how many calls are in flight, retries an HTTP 429 with backoff,
/// counts content-filter refusals, and records each call's latency and token usage against the claim being processed.
/// Anything else that fails is rethrown for the extractor or assessor to handle as it would in production.
/// </summary>
public sealed class MeteringChatClient(IChatClient inner, CallPolicy policy) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var usage = CaseUsage.Current;
        var list = messages as IList<ChatMessage> ?? [.. messages];
        var watch = Stopwatch.StartNew();
        for (var attempt = 0; ; attempt++)
        {
            await policy.Gate.WaitAsync(cancellationToken);
            try
            {
                var response = await base.GetResponseAsync(list, options, cancellationToken);
                if (response.FinishReason == ChatFinishReason.ContentFilter)
                {
                    usage?.AddContentFilter();
                }

                usage?.AddCall(watch.Elapsed.TotalMilliseconds, response.Usage);
                return response;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && CallPolicy.IsRateLimited(ex) && attempt < policy.MaxRetries)
            {
                usage?.AddRateLimitRetry();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (CallPolicy.IsContentFilter(ex))
                {
                    usage?.AddContentFilter();
                }

                usage?.AddFailedCall();
                throw;
            }
            finally
            {
                policy.Gate.Release(); // Never held while waiting to retry.
            }

            await policy.Delay(policy.BackoffFor(attempt), cancellationToken);
        }
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The evaluation does not stream.");
}
