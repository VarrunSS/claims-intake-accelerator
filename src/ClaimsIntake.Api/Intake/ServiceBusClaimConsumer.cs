using System.Diagnostics;
using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using ClaimsIntake.Api.Telemetry;
using ClaimsIntake.Domain.Telemetry;

namespace ClaimsIntake.Api.Intake;

/// <summary>
/// Azure mode: reads proposed claims from the claims-for-review Service Bus queue and gives them to
/// <see cref="ClaimMessageProcessor"/>. Runs only when <c>Intake:ServiceBus:FullyQualifiedNamespace</c> is set.
/// Authenticates with <c>DefaultAzureCredential</c>, with no keys. A message that is not a valid claim is
/// dead-lettered with a reason; a transient failure abandons the message so Service Bus redelivers it.
/// It reports its state to <see cref="ConsumerState"/>, which the readiness check reads (OBS-04).
/// Compiled only: it has not run against a real queue.
/// </summary>
public sealed class ServiceBusClaimConsumer(
    IConfiguration configuration,
    ClaimMessageProcessor processor,
    ConsumerState state,
    ILogger<ServiceBusClaimConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ns = configuration["Intake:ServiceBus:FullyQualifiedNamespace"];
        var queue = configuration["Intake:ServiceBus:QueueName"] is { Length: > 0 } q ? q : "claims-for-review";
        if (string.IsNullOrWhiteSpace(ns))
        {
            return;
        }

        try
        {
            TokenCredential credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = configuration["Intake:ManagedIdentityClientId"] is { Length: > 0 } id ? id : null,
            });

            await using var client = new ServiceBusClient(ns, credential);
            await using var serviceBusProcessor = client.CreateProcessor(queue, new ServiceBusProcessorOptions { MaxConcurrentCalls = 4 });
            serviceBusProcessor.ProcessMessageAsync += args => processor.HandleAsync(
                args.Message,
                (reason, description, ct) => args.DeadLetterMessageAsync(args.Message, reason, description, ct),
                args.CancellationToken);
            serviceBusProcessor.ProcessErrorAsync += args =>
            {
                state.RecordError();
                logger.LogError("Service Bus processing error from {Source}: {ErrorType}", args.ErrorSource, args.Exception.GetType().Name);
                return Task.CompletedTask;
            };

            await serviceBusProcessor.StartProcessingAsync(stoppingToken);
            state.MarkRunning();
            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }

            await serviceBusProcessor.StopProcessingAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The consumer could not start or died: readiness goes Unhealthy so the platform and a person can see it, instead of
            // an API that answers requests while nothing reads the queue.
            state.MarkStopped(ex.GetType().Name);
            logger.LogCritical("The Service Bus consumer stopped: {ErrorType}", ex.GetType().Name);
            throw;
        }
        finally
        {
            state.MarkStopped(null);
        }
    }
}

/// <summary>
/// Handles one message from the review queue: continues the sender's trace, accepts the claim, dead-letters one that is not a
/// valid claim. Separate from the Service Bus processor so the whole path, trace propagation included, is tested with a
/// message built in memory.
/// </summary>
public sealed class ClaimMessageProcessor(ClaimIntakeService intake, ConsumerState state)
{
    /// <summary>Settles a message as dead-lettered: reason, description, cancellation token.</summary>
    public delegate Task DeadLetter(string reason, string description, CancellationToken cancellationToken);

    public async Task HandleAsync(ServiceBusReceivedMessage message, DeadLetter deadLetter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(deadLetter);

        // The consumer span is a child of the sender's span, so the Function, the queue and the API are one trace (OBS-02). A
        // message without a context (an older sender) starts a trace of its own.
        var parent = TraceContextCarrier.Extract(message.ApplicationProperties);
        using var activity = parent is { } context
            ? ApiTelemetry.Source.StartActivity("claims.intake", ActivityKind.Consumer, context)
            : ApiTelemetry.Source.StartActivity("claims.intake", ActivityKind.Consumer);
        activity?.SetTag("messaging.system", "servicebus");
        activity?.SetTag("messaging.operation", "process");
        // The message id is the claim id (set by the sender). It is only tagged when it is one, never as free text.
        if (Guid.TryParse(message.MessageId, out var claimId))
        {
            activity?.SetTag("claimId", claimId.ToString("D"));
        }

        try
        {
            var result = await intake.AcceptJsonAsync(message.Body.ToString(), cancellationToken);
            if (result.Outcome == IntakeOutcome.Invalid)
            {
                ApiMetrics.DeadLettered.Add(1, new KeyValuePair<string, object?>("reason", "InvalidProposedClaim"));
                activity?.SetStatus(ActivityStatusCode.Error, "InvalidProposedClaim");
                await deadLetter("InvalidProposedClaim", string.Join(" ", result.Problems ?? []), cancellationToken);
            }

            state.RecordSuccess();
            // Accepted and Duplicate complete the message. An exception above abandons it for redelivery.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            state.RecordError();
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            activity?.SetTag("error.type", ex.GetType().Name);
            throw;
        }
    }
}

/// <summary>What the Service Bus consumer is doing, for the readiness check.</summary>
public sealed class ConsumerState(TimeProvider time)
{
    private readonly object _gate = new();
    private bool _running;
    private string? _stoppedBecause;
    private DateTimeOffset? _lastError;
    private DateTimeOffset? _lastSuccess;

    public void MarkRunning()
    {
        lock (_gate)
        {
            _running = true;
            _stoppedBecause = null;
        }
    }

    /// <summary>The consumer stopped. <paramref name="errorType"/> is the exception type when it failed, null for a clean stop.</summary>
    public void MarkStopped(string? errorType)
    {
        lock (_gate)
        {
            _running = false;
            _stoppedBecause = errorType ?? _stoppedBecause;
        }
    }

    public void RecordError()
    {
        lock (_gate)
        {
            _lastError = time.GetUtcNow();
        }
    }

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _lastSuccess = time.GetUtcNow();
        }
    }

    public ConsumerSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new ConsumerSnapshot(_running, _stoppedBecause, _lastError, _lastSuccess);
        }
    }
}

public sealed record ConsumerSnapshot(bool Running, string? StoppedBecause, DateTimeOffset? LastError, DateTimeOffset? LastSuccess);
