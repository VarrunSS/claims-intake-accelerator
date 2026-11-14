using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaimsIntake.Evals;

/// <summary>What a model costs, in GBP per million tokens, and where that number came from.</summary>
/// <param name="Status"><c>verified</c> only when copied from an official page on <paramref name="AsOf"/>; otherwise <c>verify-before-use</c>.</param>
public sealed record ModelPrice(string Currency, decimal InputPer1M, decimal OutputPer1M, string SourceUrl, string AsOf, string Status)
{
    public bool IsVerified => Status.Equals(ModelRegistry.PriceVerified, StringComparison.OrdinalIgnoreCase);

    /// <summary>The cost of one call (or a sum of calls) with the given token counts.</summary>
    public decimal Cost(long inputTokens, long outputTokens) =>
        ((inputTokens * InputPer1M) + (outputTokens * OutputPer1M)) / 1_000_000m;
}

/// <summary>One candidate model. No keys live here: authentication is <c>DefaultAzureCredential</c>.</summary>
/// <param name="Kind"><c>azure-openai</c>, <c>azure-ai-inference</c> or <c>foundry-anthropic</c>.</param>
/// <param name="Endpoint">The resource endpoint (a placeholder until filled in). For <c>foundry-anthropic</c> it is the Foundry resource name.</param>
/// <param name="Deployment">The deployment name in the Foundry project (the model name for Claude).</param>
/// <param name="Enabled">False until the model is deployed and the endpoint filled in. A disabled entry is reported "not yet run".</param>
public sealed record ModelEntry(
    string Id, string DisplayName, string Kind, string Endpoint, string Deployment, bool Enabled, ModelPrice Price, string? Notes, bool IsFake = false);

/// <summary>Reads <c>evals/models.json</c> and checks it.</summary>
public sealed class ModelRegistry(IReadOnlyList<ModelEntry> models)
{
    public const string PriceVerified = "verified";
    public const string FakeOracle = "fake-oracle";
    public const string FakeNoisy = "fake-noisy";

    public static readonly IReadOnlySet<string> Kinds = new HashSet<string>(StringComparer.Ordinal) { "azure-openai", "azure-ai-inference", "foundry-anthropic" };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public IReadOnlyList<ModelEntry> Models { get; } = models;

    /// <summary>
    /// The two fake models, for demonstrating the pipeline with no credentials. Their prices are invented round numbers and
    /// they are never part of <c>models.json</c>; they are reachable only with <c>--dev-fakes</c>.
    /// </summary>
    public static IReadOnlyList<ModelEntry> Fakes { get; } =
    [
        new(FakeOracle, "FAKE oracle (returns the ground truth)", "fake", "none", FakeOracle, true,
            new ModelPrice("GBP", 2.00m, 8.00m, "none (invented)", "n/a", "fake"), "FAKE: pipeline demonstration, not a model result.", true),
        new(FakeNoisy, "FAKE noisy (oracle plus scripted faults)", "fake", "none", FakeNoisy, true,
            new ModelPrice("GBP", 0.50m, 2.00m, "none (invented)", "n/a", "fake"), "FAKE: pipeline demonstration, not a model result.", true),
    ];

    public static ModelRegistry Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The model registry \"{path}\" does not exist. Pass --registry or create evals/models.json.", path);
        }

        return Parse(File.ReadAllText(path));
    }

    public static ModelRegistry Parse(string json)
    {
        var file = JsonSerializer.Deserialize<RegistryFile>(json, Json) ?? throw new InvalidDataException("The model registry is empty.");
        var entries = file.Models ?? throw new InvalidDataException("The model registry has no \"models\" list.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in entries)
        {
            Validate(m);
            if (!seen.Add(m.Id))
            {
                throw new InvalidDataException($"Model id \"{m.Id}\" appears twice.");
            }
        }

        return new ModelRegistry(entries);
    }

    /// <summary>Finds a model by id. The fakes are found only when <paramref name="allowFakes"/> is set.</summary>
    public ModelEntry Find(string id, bool allowFakes)
    {
        var fake = Fakes.FirstOrDefault(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (fake is not null)
        {
            return allowFakes
                ? fake
                : throw new ArgumentException($"\"{id}\" is a fake model for pipeline demonstrations. It needs the --dev-fakes flag.");
        }

        return Models.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown model \"{id}\". The registry has: {string.Join(", ", Models.Select(m => m.Id))}.");
    }

    private static void Validate(ModelEntry m)
    {
        static void Need(bool ok, string id, string what)
        {
            if (!ok)
            {
                throw new InvalidDataException($"Model \"{id}\": {what}.");
            }
        }

        var id = m.Id ?? "(no id)";
        Need(!string.IsNullOrWhiteSpace(m.Id) && m.Id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'), id, "id must be letters, digits, '-', '_' or '.'");
        Need(Kinds.Contains(m.Kind ?? ""), id, $"kind must be one of {string.Join(", ", Kinds)}");
        Need(!string.IsNullOrWhiteSpace(m.Deployment), id, "deployment is required");
        Need(!string.IsNullOrWhiteSpace(m.Endpoint), id, "endpoint is required (a placeholder is fine while disabled)");
        Need(m.Price is not null, id, "price is required");
        Need(m.Price!.Currency == "GBP", id, "price currency must be GBP");
        Need(m.Price.InputPer1M >= 0 && m.Price.OutputPer1M >= 0, id, "prices cannot be negative");
        Need(!string.IsNullOrWhiteSpace(m.Price.SourceUrl) && !string.IsNullOrWhiteSpace(m.Price.AsOf), id, "price needs a sourceUrl and an asOf date");
        Need(DateOnly.TryParseExact(m.Price.AsOf, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _), id, "price asOf must be yyyy-MM-dd");
        Need(m.Price.Status is PriceVerified or "verify-before-use", id, "price status must be \"verified\" or \"verify-before-use\"");
        Need(!(m.Enabled && m.Endpoint.Contains('<', StringComparison.Ordinal)), id, "an enabled model still has a placeholder endpoint");
        Need(!(m.Enabled && m.Deployment.Contains('<', StringComparison.Ordinal)), id, "an enabled model still has a placeholder deployment");
    }

    private sealed record RegistryFile(string? Description, IReadOnlyList<ModelEntry>? Models);
}
