using ClaimsIntake.Domain;

namespace ClaimsIntake.Api.Claims;

/// <summary>
/// Made-up claims for running the API locally, enabled with Seed:DemoData. All names and policy
/// numbers are fictional and match the seed data in ClaimsIntake.Legacy.
/// </summary>
public static class DemoData
{
    public static IReadOnlyList<Claim> Create(TimeProvider time)
    {
        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        // Policy TM-CP-100001 runs for the year to 2026-12-31 in the Legacy seed data.
        var covered = new PolicyReference("TM-CP-100001", "Brindle & Marsh Joinery Ltd", new(2026, 1, 1), new(2026, 12, 31));

        return
        [
            Build("inbox/2026-11-12-brindle-water-damage.pdf", now.AddHours(-30), covered, today,
                Field(ClaimFields.PolicyNumber, "TM-CP-100001", 0.98),
                Field(ClaimFields.ClaimantName, "Brindle & Marsh Joinery Ltd", 0.96),
                Field(ClaimFields.LossDate, "2026-11-03", 0.94),
                Field(ClaimFields.LossDescription, "Burst pipe in the workshop store room overnight; stock damaged.", 0.91),
                Field(ClaimFields.EstimatedAmount, "12,500", 0.88)),

            Build("inbox/2026-11-12-handwritten-note.pdf", now.AddHours(-20), null, today,
                Field(ClaimFields.PolicyNumber, null, 0.0),
                Field(ClaimFields.ClaimantName, "R. Okafor", 0.62),
                Field(ClaimFields.LossDate, "last Tuesday", 0.40),
                Field(ClaimFields.LossDescription, "Van broken into, tools taken.", 0.70)),

            Build("inbox/2026-11-13-late-notification.eml", now.AddHours(-6), covered, today,
                Field(ClaimFields.PolicyNumber, "TM-CP-100001", 0.97),
                Field(ClaimFields.ClaimantName, "Brindle & Marsh Joinery Ltd", 0.95),
                Field(ClaimFields.LossDate, today.AddDays(9).ToString("yyyy-MM-dd"), 0.93),
                Field(ClaimFields.LossDescription, "Fire in the spray booth.", 0.90)),

            Build("inbox/2026-11-13-low-confidence-scan.pdf", now.AddHours(-2), covered, today,
                Field(ClaimFields.PolicyNumber, "TM-CP-100001", 0.99),
                Field(ClaimFields.ClaimantName, "Brindle & Marsh Joinery Ltd", 0.97),
                Field(ClaimFields.LossDate, "03/11/2026", 0.95),
                Field(ClaimFields.LossDescription, "Theft of a ladder rack and power tools from the yard.", 0.93),
                Field(ClaimFields.EstimatedAmount, "3,2OO", 0.55)),
        ];
    }

    private static ExtractedField Field(string name, string? value, double confidence) =>
        new(name, value, confidence, value is null ? null : new SourceSpan("demo", 1, 0, value.Length));

    private static Claim Build(
        string document, DateTimeOffset receivedAt, PolicyReference? policy, DateOnly today, params ExtractedField[] fields)
    {
        var byName = fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
        return new Claim(Guid.NewGuid(), document, receivedAt, fields, ClaimValidator.Validate(byName, policy, today));
    }
}
