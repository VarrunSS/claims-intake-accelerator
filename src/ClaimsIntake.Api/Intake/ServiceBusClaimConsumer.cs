using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;

namespace ClaimsIntake.Api.Intake;

/// <summary>
/// Azure mode: reads proposed claims from the claims-for-review Service Bus queue and gives them to
/// <see cref="ClaimIntakeService"/>. Runs only when <c>Intake:ServiceBus:FullyQualifiedNamespace</c> is set.
/// Authenticates with <c>DefaultAzureCredential</c>, with no keys. A message that is not a valid claim is
/// dead-lettered with a reason; a transient failure abandons the message so Service Bus redelivers it.
/// Compiled only: it has not run against a real queue.
/// </summary>
public sealed class ServiceBusClaimConsumer(
    IConfiguration configuration,
    ClaimIntakeService intake,
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

        TokenCredential credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = configuration["Intake:ManagedIdentityClientId"] is { Length: > 0 } id ? id : null,
        });

        await using var client = new ServiceBusClient(ns, credential);
        await using var processor = client.CreateProcessor(queue, new ServiceBusProcessorOptions { MaxConcurrentCalls = 4 });
        processor.ProcessMessageAsync += HandleMessageAsync;
        processor.ProcessErrorAsync += args =>
        {
            logger.LogError("Service Bus processing error from {Source}: {ErrorType}", args.ErrorSource, args.Exception.GetType().Name);
            return Task.CompletedTask;
        };

        await processor.StartProcessingAsync(stoppingToken);
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }

        await processor.StopProcessingAsync(CancellationToken.None);
    }

    private async Task HandleMessageAsync(ProcessMessageEventArgs args)
    {
        var result = await intake.AcceptJsonAsync(args.Message.Body.ToString(), args.CancellationToken);
        if (result.Outcome == IntakeOutcome.Invalid)
        {
            await args.DeadLetterMessageAsync(args.Message, "InvalidProposedClaim",
                string.Join(" ", result.Problems ?? []), args.CancellationToken);
        }

        // Accepted and Duplicate complete the message. An exception above abandons it for redelivery.
    }
}
