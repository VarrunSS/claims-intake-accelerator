using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaimsIntake.Evals;

/// <summary>An existing claim a case's document may duplicate. Supplied by the case, so the result does not depend on any store.</summary>
public sealed record ExistingClaimSpec(
    string Reference, string Source, string? PolicyNumber, string? LossDate, decimal? AmountGbp, string? Status);

/// <summary>What the pipeline is told about a case besides the document.</summary>
/// <param name="ReceivedAt">When the document "arrived": the received timestamp passed to the pipeline, and the clock it runs on.</param>
/// <param name="ExistingClaims">Claims the duplicate check will find.</param>
/// <param name="DuplicateOf">Id of another case whose file was "already received", for the duplicate-file check.</param>
/// <param name="MaxDocumentBytes">A smaller size limit for this case, so an oversized document need not be committed.</param>
public sealed record CaseContext(
    DateTimeOffset ReceivedAt, IReadOnlyList<ExistingClaimSpec> ExistingClaims, string? DuplicateOf, int? MaxDocumentBytes);

public sealed record ExpectedFields(
    [property: JsonPropertyName("policy_number")] string? PolicyNumber,
    [property: JsonPropertyName("claimant_name")] string? ClaimantName,
    [property: JsonPropertyName("loss_date")] string? LossDate,
    [property: JsonPropertyName("cause")] string? Cause,
    [property: JsonPropertyName("estimated_amount_gbp")] decimal? EstimatedAmountGbp,
    [property: JsonPropertyName("notification_date")] string? NotificationDate);

public sealed record ExpectedFinding(string Kind, string ClauseRef)
{
    public string Key => $"{Kind}|{ClauseRef}";
}

public sealed record Expected(
    ExpectedFields Fields, IReadOnlyList<string> IssueCodes, IReadOnlyList<ExpectedFinding> Findings, string Route, string? NotificationSource);

/// <summary>One synthetic claim: the document, what the pipeline should make of it, and how it is tagged.</summary>
public sealed class EvalCase
{
    public required string Id { get; init; }
    public required string File { get; init; }
    public required string Split { get; init; }
    public string Family { get; init; } = "";
    public required IReadOnlyList<string> Tags { get; init; }
    public required CaseContext Context { get; init; }
    public required Expected Expected { get; init; }

    [JsonIgnore]
    public byte[] Content { get; set; } = [];
}

public static class Splits
{
    public const string Dev = "dev";
    public const string Test = "test";
    public const string All = "all";

    public static bool IsValid(string split) => split is Dev or Test or All;
}

/// <summary>Reads <c>&lt;id&gt;.expected.json</c> and the document it names from a dataset folder.</summary>
public static class DatasetLoader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static IReadOnlyList<EvalCase> Load(string folder, string split = Splits.All)
    {
        if (!Splits.IsValid(split))
        {
            throw new ArgumentException($"The split must be dev, test or all, not \"{split}\".", nameof(split));
        }

        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"The dataset folder \"{folder}\" does not exist.");
        }

        var cases = new List<EvalCase>();
        foreach (var path in Directory.EnumerateFiles(folder, "*.expected.json").Order(StringComparer.Ordinal))
        {
            var item = JsonSerializer.Deserialize<EvalCase>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException($"{Path.GetFileName(path)} is empty.");
            if (!Splits.IsValid(item.Split) || item.Split == Splits.All)
            {
                throw new InvalidDataException($"{item.Id}: split must be dev or test, not \"{item.Split}\".");
            }

            if (split != Splits.All && item.Split != split)
            {
                continue;
            }

            var document = Path.Combine(folder, item.File);
            item.Content = File.Exists(document)
                ? File.ReadAllBytes(document)
                : throw new FileNotFoundException($"{item.Id}: the document \"{item.File}\" is missing.", document);
            cases.Add(item);
        }

        var duplicates = cases.GroupBy(c => c.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        return duplicates.Count == 0 ? cases : throw new InvalidDataException($"Duplicate case ids: {string.Join(", ", duplicates)}.");
    }

    /// <summary>Walks up from <paramref name="start"/> until it finds <c>evals/dataset</c>.</summary>
    public static string FindDefaultFolder(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "evals", "dataset");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("Could not find evals/dataset above the current folder. Pass --dataset.");
    }
}
