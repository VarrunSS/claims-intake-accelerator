namespace ClaimsIntake.Domain;

/// <summary>Where a claim is in the intake workflow.</summary>
public enum ClaimStatus
{
    /// <summary>Fields have been extracted and the claim is waiting for a handler.</summary>
    NeedsReview = 0,

    /// <summary>A handler approved the claim. The write to the legacy system is still to come.</summary>
    Approved = 1,

    /// <summary>A handler rejected the claim. Nothing is written to the legacy system.</summary>
    Rejected = 2,

    /// <summary>The approved claim has been written to the legacy claims system.</summary>
    Submitted = 3,
}
