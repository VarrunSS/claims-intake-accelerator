using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ClaimsIntake.Api.Intake;

/// <summary>
/// Readiness of the Service Bus consumer (OBS-04). Unhealthy when it is not running (not started yet, stopped, or it died);
/// Degraded when it is running but its latest processing error is newer than its latest success and recent. A claim that waits
/// in the queue because nothing reads it is the silent failure this guards against.
/// </summary>
public sealed class ConsumerHealthCheck(ConsumerState state, TimeProvider time) : IHealthCheck
{
    /// <summary>An error older than this no longer counts, so one bad message does not mark the API degraded for ever.</summary>
    public static readonly TimeSpan ErrorWindow = TimeSpan.FromMinutes(5);

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var snapshot = state.Snapshot();
        if (!snapshot.Running)
        {
            // The exception type only: it is a probe response and must not carry detail.
            return Task.FromResult(HealthCheckResult.Unhealthy(snapshot.StoppedBecause is { } type
                ? $"The Service Bus consumer is not running ({type})."
                : "The Service Bus consumer is not running."));
        }

        if (snapshot.LastError is { } error
            && time.GetUtcNow() - error < ErrorWindow
            && (snapshot.LastSuccess is null || snapshot.LastSuccess < error))
        {
            return Task.FromResult(HealthCheckResult.Degraded("The Service Bus consumer has had an error since its last success."));
        }

        return Task.FromResult(HealthCheckResult.Healthy());
    }
}
