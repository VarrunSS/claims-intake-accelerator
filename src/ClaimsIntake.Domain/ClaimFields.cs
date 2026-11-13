namespace ClaimsIntake.Domain;

/// <summary>Names of the fields the extraction step is asked to find.</summary>
public static class ClaimFields
{
    public const string PolicyNumber = "policy_number";
    public const string ClaimantName = "claimant_name";
    public const string LossDate = "loss_date";
    public const string LossDescription = "loss_description";
    public const string EstimatedAmount = "estimated_amount_gbp";
}
