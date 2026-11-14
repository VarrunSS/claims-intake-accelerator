namespace ClaimsIntake.Domain.Intake;

/// <summary>
/// The size and shape limits of a proposed claim, defined once. The pipeline enforces them before it hands a claim over,
/// and the API checks the same numbers when it receives one, so a claim the pipeline produces can never be one the API
/// refuses (REL-04). Where a limit is exceeded the claim is cut down and a person is told, never dropped.
/// </summary>
public static class IntakeLimits
{
    public const int MaxFields = 20;
    public const int MaxIssues = 50;
    public const int MaxPages = 100;
    public const int MaxTotalCharacters = 2_000_000;
    public const int MaxValueLength = 4_000;
    public const int MaxDocumentIdLength = 300;
    public const int MaxAuditEntries = 100;
    public const int MaxAuditArguments = 12;
    public const int MaxAuditArgumentLength = 300;
    public const int MaxAuditToolLength = 80;
    public const int MaxFindings = 30;
    public const int MaxCitationsPerFinding = 10;

    /// <summary>
    /// The most the serialised hand-off message may be, in bytes. Service Bus Standard allows 256 KB; this leaves room for
    /// the envelope and for UTF-8 expansion.
    /// </summary>
    public const int MaxHandoffBytes = 200 * 1024;
}
