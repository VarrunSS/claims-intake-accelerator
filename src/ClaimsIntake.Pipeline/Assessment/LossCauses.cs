using System.Text.RegularExpressions;

namespace ClaimsIntake.Pipeline.Assessment;

/// <summary>A cause of loss the local rules recognise, and how to look it up in a policy wording.</summary>
/// <param name="Key">Stable short name: <c>fire</c>, <c>flood</c>, <c>theft</c>, <c>storm</c>, <c>impact</c>, <c>refrigeration</c>, <c>breakdown</c> or <c>water</c>.</param>
/// <param name="PerilHeading">The heading of the insured peril that covers it.</param>
/// <param name="Query">The words used to search the wording for that peril.</param>
/// <param name="Pattern">Matches a loss description that names it.</param>
public sealed record LossCause(string Key, string PerilHeading, string Query, Regex Pattern);

/// <summary>
/// Keyword classification of a loss description into one cause. It is shared by the rules assessor and by the
/// evaluation, which scores the cause as the class of the extracted loss description.
/// </summary>
public static class LossCauses
{
    // Order matters: the first cause that matches wins, so flood is tested before the generic escape of water.
    public static readonly IReadOnlyList<LossCause> All =
    [
        new("fire", "Fire", "fire lightning explosion smoke", Rx(@"\b(fire|blaze|burnt|burned|smoke|flames?)\b")),
        new("flood", "Flood", "flood inundation surface water rising groundwater", Rx(@"\b(flood(?:ed|ing|s)?|surface water|overflowing river|river burst)\b")),
        new("theft", "Theft", "theft forcible violent entry", Rx(@"\b(stolen|theft|thieves|burglar\w*|break-?in|forced entry|forcible)\b")),
        new("storm", "Storm", "storm gale rain hail snow", Rx(@"\b(storm|gale|hail|high winds?)\b")),
        new("impact", "Impact", "impact collision road vehicle", Rx(@"\b(struck|collision|collided|crash(?:ed)?|rammed|hit by)\b")),
        new("refrigeration", "Refrigeration failure", "refrigeration failure plant stock", Rx(@"\b(freezer|refrigerat\w*|chiller|cold room)\b")),
        new("breakdown", "Breakdown", "breakdown mechanical electrical failure plant hydraulic", Rx(@"\b(breakdown|failed|failure|seized|malfunction\w*)\b")),
        new("water", "Escape of water", "escape of water burst leaking pipe tank", Rx(@"\b(burst|leak(?:ed|ing|s)?|pipe|escape of water|water)\b")),
    ];

    /// <summary>The first cause whose pattern matches the description, or null when none does.</summary>
    public static LossCause? Classify(string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : All.FirstOrDefault(c => c.Pattern.IsMatch(description));

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}
