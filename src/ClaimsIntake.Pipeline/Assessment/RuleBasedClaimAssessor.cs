using System.Globalization;
using System.Text.RegularExpressions;
using ClaimsIntake.Domain.Assessment;
using ClaimsIntake.Pipeline.Policy;
using ClaimsIntake.Pipeline.Tools;

namespace ClaimsIntake.Pipeline.Assessment;

/// <summary>
/// The local, offline assessor. It calls the same tools the agent has, in a fixed order, so the same claim always
/// gets the same assessment: look up the policy, search its wording with the cause of loss, check the exclusion
/// clauses that come back, read the notification period from the wording and compare it with the dates, check for
/// a duplicate. Every clause it cites was returned by <c>search_policy_wording</c> in this run.
///
/// It is a deliberately simple baseline: it recognises a handful of causes and exclusions by keyword, so it will miss
/// what the words do not say. It points at clauses and never decides cover (ADR 0006).
/// </summary>
public sealed partial class RuleBasedClaimAssessor : IClaimAssessor
{
    public const string AssessorName = "rules";

    /// <summary>The longest period a building may be unoccupied before the wording's unoccupied-buildings exclusion applies.</summary>
    private const int UnoccupiedLimitDays = 30;

    private static readonly Regex Flood = Rx(@"\b(flood(?:ed|ing|s)?|surface water|overflowing river|river burst)\b");
    private static readonly Regex WearAndTear = Rx(@"\b(wear and tear|worn|wearing|gradual(?:ly)?|deteriorat\w*|corro\w*|rust(?:ed|y)?|rotted|rotten|perished|over time)\b");
    private static readonly Regex UnoccupiedBefore = Rx(@"\b(?:unoccupied|vacant|empty|closed|shut|not (?:been )?(?:used|occupied))\b[^.]{0,60}?\b(?<n>\d{1,3})\s*(?<unit>day|week|month)s?\b");
    private static readonly Regex UnoccupiedAfter = Rx(@"\b(?<n>\d{1,3})\s*(?<unit>day|week|month)s?\b[^.]{0,40}?\b(?:unoccupied|vacant|empty|closed|shut)\b");
    private static readonly Regex WithinDays = Rx(@"within\s+(?<n>\d{1,3})\s+days");

    public string Name => AssessorName;

    public async Task<ClaimAssessment> AssessAsync(AssessmentContext context, ClaimTools tools, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tools);
        var findings = new List<CoverageFinding>();
        var notes = new List<string>();

        var policy = context.PolicyNumber is { } number ? await tools.LookupPolicyAsync(number, cancellationToken) : null;
        if (policy is null)
        {
            notes.Add("Cover was not assessed because the policy could not be identified.");
            return new ClaimAssessment(AssessorName, [], ReviewRoute.StraightToHandler,
                "The policy could not be identified, so the data issues have to be fixed before cover can be assessed.", notes);
        }

        var description = context.Description ?? "";
        var cause = LossCauses.Classify(description);

        // 1. Cover: the insured peril that matches the cause of loss.
        if (cause is not null)
        {
            var hits = await tools.SearchPolicyWordingAsync(cause.Query, policy.WordingId, 6, cancellationToken);
            var peril = hits.FirstOrDefault(h => IsSection(h, "Insured perils") && h.Heading.Contains(cause.PerilHeading, StringComparison.OrdinalIgnoreCase));
            if (peril is not null)
            {
                findings.Add(new CoverageFinding(FindingKind.CoverageClause,
                    $"The loss appears to fall under clause {peril.ClauseRef} ({peril.Heading}).", [Cite(peril)]));
            }
        }

        // 2. Exclusions: each one the description points to, confirmed by the clause the search returns.
        if (Flood.IsMatch(description))
        {
            await AddExclusion(findings, tools, policy.WordingId, "flood inundation surface water rising groundwater", "Flood", "flood",
                "The description mentions flooding and the wording excludes flood", cancellationToken);
        }

        if (WearAndTear.IsMatch(description))
        {
            await AddExclusion(findings, tools, policy.WordingId, "wear and tear gradual deterioration corrosion", "Wear", "wear",
                "The description suggests wear, corrosion or gradual deterioration", cancellationToken);
        }

        if (UnoccupiedDays(description) is { } days && days > UnoccupiedLimitDays && cause?.Key is "water" or "theft")
        {
            await AddExclusion(findings, tools, policy.WordingId, "unoccupied buildings consecutive days", "Unoccupied", "unoccupied",
                $"The description says the premises were unoccupied for about {days} days", cancellationToken);
        }

        // 3. Notification period: read from the wording, not assumed.
        if (context.LossDate is { } lossDate)
        {
            await CheckNotification(findings, notes, tools, policy.WordingId, lossDate, context.NotifiedOn, cancellationToken);
        }

        // 4. Duplicates.
        if (context.LossDate is { } date)
        {
            var duplicate = await tools.CheckDuplicateAsync(policy.PolicyNumber, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), context.Amount, cancellationToken);
            foreach (var match in duplicate.Matches)
            {
                findings.Add(new CoverageFinding(FindingKind.PossibleDuplicate,
                    $"An existing claim ({match.Reference}, {match.Source}) has the same policy and loss date and a similar amount.", [], match.Reference));
            }
        }

        if (cause is null)
        {
            notes.Add("The cause of loss was not recognised, so no cover clause was matched.");
        }
        else if (!findings.Any(f => f.Kind is FindingKind.CoverageClause or FindingKind.PossibleExclusion))
        {
            notes.Add("No clause of the wording clearly covers or excludes this cause of loss.");
        }

        var (route, reason) = ChooseRoute(findings, notes);
        await tools.ProposeClaimAsync(policy.PolicyNumber, context.LossDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
            context.Amount, route == ReviewRoute.SeniorReview ? "senior_review" : "straight_to_handler", reason, cancellationToken);
        return new ClaimAssessment(AssessorName, findings, route, reason, notes);
    }

    private static async Task AddExclusion(
        List<CoverageFinding> findings, ClaimTools tools, string? wordingId, string query, string headingWord, string textWord,
        string because, CancellationToken cancellationToken)
    {
        var hits = await tools.SearchPolicyWordingAsync(query, wordingId, 5, cancellationToken);
        var clause = hits.FirstOrDefault(h => IsSection(h, "Exclusions")
            && (h.Heading.Contains(headingWord, StringComparison.OrdinalIgnoreCase) || h.Text.Contains(textWord, StringComparison.OrdinalIgnoreCase)));
        if (clause is not null)
        {
            findings.Add(new CoverageFinding(FindingKind.PossibleExclusion,
                $"{because}: clause {clause.ClauseRef} ({clause.Heading}) may exclude this loss.", [Cite(clause)]));
        }
    }

    private static async Task CheckNotification(
        List<CoverageFinding> findings, List<string> notes, ClaimTools tools, string? wordingId,
        DateOnly lossDate, DateOnly notifiedOn, CancellationToken cancellationToken)
    {
        var hits = await tools.SearchPolicyWordingAsync("notification of claims notify days date of loss", wordingId, 5, cancellationToken);
        var clause = hits.FirstOrDefault(h => IsSection(h, "Conditions") && h.Heading.Contains("Notification", StringComparison.OrdinalIgnoreCase));
        if (clause is null || WithinDays.Match(clause.Text) is not { Success: true } m)
        {
            notes.Add("The notification period could not be read from the wording.");
            return;
        }

        var limit = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
        var elapsed = notifiedOn.DayNumber - lossDate.DayNumber;
        if (elapsed > limit)
        {
            findings.Add(new CoverageFinding(FindingKind.NotificationBreach,
                $"The claim was notified {elapsed} days after the loss; clause {clause.ClauseRef} allows {limit} days.", [Cite(clause)]));
        }
    }

    private static (ReviewRoute Route, string Reason) ChooseRoute(List<CoverageFinding> findings, List<string> notes)
    {
        var reasons = new List<string>();
        if (findings.Any(f => f.Kind == FindingKind.PossibleExclusion))
        {
            reasons.Add("a possible exclusion");
        }

        if (findings.Any(f => f.Kind == FindingKind.NotificationBreach))
        {
            reasons.Add("a late notification");
        }

        if (findings.Any(f => f.Kind == FindingKind.PossibleDuplicate))
        {
            reasons.Add("a possible duplicate");
        }

        if (reasons.Count == 0 && !findings.Any(f => f.Kind == FindingKind.CoverageClause) && notes.Count > 0)
        {
            reasons.Add("no clear cover clause");
        }

        return reasons.Count > 0
            ? (ReviewRoute.SeniorReview, $"Needs senior review: {string.Join(", ", reasons)}.")
            : (ReviewRoute.StraightToHandler, "A cover clause matches and no exclusion, late notification or duplicate was found.");
    }

    private static int? UnoccupiedDays(string description)
    {
        var match = UnoccupiedBefore.Match(description);
        if (!match.Success)
        {
            match = UnoccupiedAfter.Match(description);
        }

        if (!match.Success)
        {
            return null;
        }

        var n = int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
        return match.Groups["unit"].Value.ToLowerInvariant() switch
        {
            "week" => n * 7,
            "month" => n * 30,
            _ => n,
        };
    }

    private static bool IsSection(ClauseHit hit, string section) => hit.Section.Equals(section, StringComparison.OrdinalIgnoreCase);

    private static ClauseCitation Cite(ClauseHit hit) => new(hit.WordingId, hit.ClauseRef, hit.Heading, hit.Text);

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}
