namespace ClaimsIntake.Legacy;

/// <summary>A policy as the legacy system returns it. <c>WordingId</c> names the policy wording it is written on (samples/policy-wordings).</summary>
public sealed record LegacyPolicy(
    string Number,
    string InsuredName,
    string Product,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal SumInsuredGbp,
    decimal ExcessGbp,
    string WordingId);

public interface IPolicyStore
{
    LegacyPolicy? Find(string number);
}

/// <summary>Ten made-up commercial policies. Every name and number is fictional.</summary>
public sealed class InMemoryPolicyStore : IPolicyStore
{
    private readonly Dictionary<string, LegacyPolicy> _policies = Seed().ToDictionary(p => p.Number, StringComparer.Ordinal);

    public LegacyPolicy? Find(string number) =>
        _policies.GetValueOrDefault(number.Trim().ToUpperInvariant());

    private static IEnumerable<LegacyPolicy> Seed() =>
    [
        new("TM-CP-100001", "Brindle & Marsh Joinery Ltd", "Commercial Combined", new(2026, 1, 1), new(2026, 12, 31), 750_000m, 500m, "TM-PW-CC-2026"),
        new("TM-CP-100002", "Kestrel Print & Packaging Ltd", "Commercial Combined", new(2026, 3, 1), new(2027, 2, 28), 1_200_000m, 1_000m, "TM-PW-CC-2026"),
        new("TM-CP-100003", "Harcourt Dental Practice", "Commercial Combined", new(2026, 6, 15), new(2027, 6, 14), 400_000m, 250m, "TM-PW-CC-2026"),
        new("TM-CP-100004", "Lowther Fabrication Works Ltd", "Engineering", new(2025, 11, 1), new(2026, 10, 31), 2_500_000m, 2_500m, "TM-PW-EN-2026"),
        new("TM-CP-100005", "Pemberton Road Haulage Ltd", "Motor Fleet", new(2026, 4, 1), new(2027, 3, 31), 1_800_000m, 750m, "TM-PW-CC-2026"),
        new("TM-CP-100006", "Wren & Co Estate Agents", "Office", new(2026, 2, 1), new(2027, 1, 31), 300_000m, 250m, "TM-PW-OF-2026"),
        new("TM-CP-100007", "Tideway Catering Supplies Ltd", "Commercial Combined", new(2026, 9, 1), new(2027, 8, 31), 950_000m, 500m, "TM-PW-CCF-2026"),
        new("TM-CP-100008", "Ashdown Veterinary Group", "Commercial Combined", new(2026, 1, 15), new(2027, 1, 14), 650_000m, 500m, "TM-PW-CC-2026"),
        new("TM-CP-100009", "Greyfriars Bookbinders", "Office", new(2025, 12, 1), new(2026, 11, 30), 180_000m, 100m, "TM-PW-OF-2026"),
        new("TM-CP-100010", "Millbrook Garden Centres Ltd", "Commercial Combined", new(2026, 5, 1), new(2027, 4, 30), 2_100_000m, 1_000m, "TM-PW-CCF-2026"),
    ];
}
