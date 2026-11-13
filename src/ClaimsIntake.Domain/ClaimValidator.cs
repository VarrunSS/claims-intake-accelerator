using System.Globalization;

namespace ClaimsIntake.Domain;

/// <summary>
/// Data-quality rules applied to an extracted claim before a handler sees it. The rules are pure:
/// the caller supplies the policy (if found) and today's date, so results are repeatable.
/// </summary>
public static class ClaimValidator
{
    /// <summary>Fields below this confidence are flagged for a closer look.</summary>
    public const double LowConfidenceThreshold = 0.80;

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "d MMMM yyyy", "d MMM yyyy"];

    /// <summary>Parses a loss date written as ISO 8601 or one of the UK day-first formats seen in the sample emails.</summary>
    public static bool TryParseDate(string? text, out DateOnly date) =>
        DateOnly.TryParseExact(text?.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <param name="fields">The extracted fields of the claim.</param>
    /// <param name="policy">The policy the claim names, or null if it was not found in the legacy system.</param>
    /// <param name="today">Today's date in the UK. Passed in so tests do not depend on the clock.</param>
    public static IReadOnlyList<ValidationIssue> Validate(
        IReadOnlyDictionary<string, ExtractedField> fields,
        PolicyReference? policy,
        DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var issues = new List<ValidationIssue>();

        CheckPolicyNumber(fields, policy, issues);
        CheckLossDate(fields, policy, today, issues);
        CheckAmount(fields, issues);
        CheckConfidence(fields, issues);

        return issues;
    }

    private static void CheckPolicyNumber(
        IReadOnlyDictionary<string, ExtractedField> fields, PolicyReference? policy, List<ValidationIssue> issues)
    {
        if (!fields.TryGetValue(ClaimFields.PolicyNumber, out var field) || !field.HasValue)
        {
            issues.Add(new(IssueCode.MissingPolicyNumber, IssueSeverity.Error, ClaimFields.PolicyNumber,
                "No policy number was found in the document."));
        }
        else if (policy is null)
        {
            issues.Add(new(IssueCode.UnknownPolicy, IssueSeverity.Error, ClaimFields.PolicyNumber,
                $"Policy {field.Value!.Trim()} was not found in the claims system."));
        }
    }

    private static void CheckLossDate(
        IReadOnlyDictionary<string, ExtractedField> fields, PolicyReference? policy, DateOnly today, List<ValidationIssue> issues)
    {
        if (!fields.TryGetValue(ClaimFields.LossDate, out var field) || !field.HasValue)
        {
            issues.Add(new(IssueCode.MissingLossDate, IssueSeverity.Error, ClaimFields.LossDate,
                "No date of loss was found in the document."));
            return;
        }

        if (!TryParseDate(field.Value, out var lossDate))
        {
            issues.Add(new(IssueCode.UnreadableLossDate, IssueSeverity.Error, ClaimFields.LossDate,
                $"The date of loss \"{field.Value!.Trim()}\" could not be read as a date."));
            return;
        }

        if (lossDate > today)
        {
            issues.Add(new(IssueCode.LossDateInFuture, IssueSeverity.Error, ClaimFields.LossDate,
                $"The date of loss {lossDate:yyyy-MM-dd} is in the future."));
        }

        if (policy is not null && !policy.Covers(lossDate))
        {
            issues.Add(new(IssueCode.LossDateOutsidePolicyPeriod, IssueSeverity.Error, ClaimFields.LossDate,
                $"The date of loss {lossDate:yyyy-MM-dd} is outside the policy period " +
                $"{policy.PeriodStart:yyyy-MM-dd} to {policy.PeriodEnd:yyyy-MM-dd}."));
        }
    }

    private static void CheckAmount(IReadOnlyDictionary<string, ExtractedField> fields, List<ValidationIssue> issues)
    {
        if (!fields.TryGetValue(ClaimFields.EstimatedAmount, out var field) || !field.HasValue)
        {
            return; // The amount is optional at intake; handlers often add it later.
        }

        var text = field.Value!.Replace("£", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal).Trim();
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount < 0)
        {
            issues.Add(new(IssueCode.InvalidAmount, IssueSeverity.Warning, ClaimFields.EstimatedAmount,
                $"The estimated amount \"{field.Value.Trim()}\" is not a valid positive amount."));
        }
    }

    private static void CheckConfidence(IReadOnlyDictionary<string, ExtractedField> fields, List<ValidationIssue> issues)
    {
        foreach (var field in fields.Values.Where(f => f.HasValue && f.Confidence < LowConfidenceThreshold))
        {
            issues.Add(new(IssueCode.LowConfidence, IssueSeverity.Warning, field.Name,
                $"The value for {field.Name} was extracted with low confidence ({field.Confidence:P0})."));
        }
    }
}
