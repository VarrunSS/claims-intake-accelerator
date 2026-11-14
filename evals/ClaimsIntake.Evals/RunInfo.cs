namespace ClaimsIntake.Evals;

/// <summary>A prompt as used in a run: which version, and the hash of its text, so a result can be tied to the exact words.</summary>
public sealed record PromptInfo(string Kind, string Version, string Sha256);

/// <summary>What was run: the configuration a result belongs to. Recorded in results.json.</summary>
/// <param name="Label">Row name in a comparison, for example <c>gpt-mid@v2</c> or <c>baseline</c>.</param>
/// <param name="ModelId">The registry id, or null for the offline baseline.</param>
/// <param name="IsFake">A fake model. Its numbers are a pipeline demonstration, never a model result.</param>
/// <param name="Prompts">The prompt versions used by the model-backed stages. Empty for the offline baseline.</param>
/// <param name="Price">The price the cost was computed with, if a model was used.</param>
public sealed record RunInfo(string Label, string? ModelId, string? ModelKind, bool IsFake, IReadOnlyList<PromptInfo> Prompts, ModelPrice? Price)
{
    public static RunInfo Baseline { get; } = new("baseline", null, null, false, [], null);

    public bool IsModelRun => ModelId is not null;

    public bool IsRealModel => IsModelRun && !IsFake;
}

/// <summary>Token, cost, latency and reliability totals for the model calls in a run.</summary>
public sealed record ModelUsageSummary(
    int Calls, long InputTokens, long OutputTokens, bool UsageMissing,
    double? CostPerClaimGbp, double? CostPer1000ClaimsGbp, bool PriceVerified,
    int RateLimitRetries, int ContentFilterRefusals, int FailedCalls,
    int MalformedOutputs, int SchemaViolations, double? MalformedOutputRate, int GuardRemovals,
    double? CallP50Ms, double? CallP95Ms);
