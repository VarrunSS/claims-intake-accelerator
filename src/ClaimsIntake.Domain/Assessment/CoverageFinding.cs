namespace ClaimsIntake.Domain.Assessment;

/// <summary>What a coverage finding is about.</summary>
public enum FindingKind
{
    /// <summary>A clause that appears to give cover for the loss.</summary>
    CoverageClause,

    /// <summary>A clause that may exclude the loss. A person decides; the assessor only points at it.</summary>
    PossibleExclusion,

    /// <summary>The claim was notified later than the wording's notification period allows.</summary>
    NotificationBreach,

    /// <summary>An existing claim has the same policy and loss date and a similar amount.</summary>
    PossibleDuplicate,
}

/// <summary>Where the assessor recommends the claim goes next. It is advice; the handler decides (ADR 0002).</summary>
public enum ReviewRoute
{
    /// <summary>Nothing in the assessment stands in the way. A claims handler can review it.</summary>
    StraightToHandler,

    /// <summary>An exclusion, a late notification, a possible duplicate or a doubt about the assessment itself.</summary>
    SeniorReview,
}

/// <summary>
/// A clause of a policy wording, quoted as <c>search_policy_wording</c> returned it. The text always comes
/// from the wording, never from the model that cited it.
/// </summary>
public sealed record ClauseCitation(string WordingId, string ClauseRef, string Heading, string Text);

/// <summary>
/// One point in the assessment. Findings of every kind except <see cref="FindingKind.PossibleDuplicate"/>
/// carry at least one citation.
/// </summary>
/// <param name="Kind">What the finding is about.</param>
/// <param name="Summary">One plain-English sentence for the handler. It holds no claim text.</param>
/// <param name="Citations">The wording clauses the finding rests on.</param>
/// <param name="RelatedClaimRef">For a possible duplicate: the reference of the existing claim.</param>
public sealed record CoverageFinding(
    FindingKind Kind,
    string Summary,
    IReadOnlyList<ClauseCitation> Citations,
    string? RelatedClaimRef = null);

/// <summary>The result of assessing one claim against its policy wording.</summary>
/// <param name="Assessor">Which assessor produced it: <c>rules</c> or <c>agent</c>.</param>
/// <param name="Findings">The findings, every cited clause checked against what the search returned.</param>
/// <param name="Route">The recommended route.</param>
/// <param name="RouteReason">Why, in one sentence.</param>
/// <param name="Notes">Things the handler should know about the assessment itself, such as dropped citations.</param>
public sealed record ClaimAssessment(
    string Assessor,
    IReadOnlyList<CoverageFinding> Findings,
    ReviewRoute Route,
    string RouteReason,
    IReadOnlyList<string> Notes);
