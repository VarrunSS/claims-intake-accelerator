namespace ClaimsIntake.Domain;

/// <summary>
/// The parts of a policy the intake checks need. Read from the legacy claims system, never written back.
/// </summary>
/// <param name="Number">Policy number, for example TM-CP-100001.</param>
/// <param name="InsuredName">Name of the insured business.</param>
/// <param name="PeriodStart">First day of cover (inclusive).</param>
/// <param name="PeriodEnd">Last day of cover (inclusive).</param>
public sealed record PolicyReference(string Number, string InsuredName, DateOnly PeriodStart, DateOnly PeriodEnd)
{
    public string Number { get; } = !string.IsNullOrWhiteSpace(Number)
        ? Number
        : throw new ArgumentException("A policy needs a number.", nameof(Number));

    public DateOnly PeriodEnd { get; } = PeriodEnd >= PeriodStart
        ? PeriodEnd
        : throw new ArgumentException("The policy period ends before it starts.", nameof(PeriodEnd));

    /// <summary>True when the date falls inside the period of cover, ends included.</summary>
    public bool Covers(DateOnly date) => date >= PeriodStart && date <= PeriodEnd;
}
