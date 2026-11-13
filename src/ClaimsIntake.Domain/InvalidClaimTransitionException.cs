namespace ClaimsIntake.Domain;

/// <summary>Thrown when an action is not allowed in the claim's current state.</summary>
public sealed class InvalidClaimTransitionException(string message) : InvalidOperationException(message);
