using Competition.Infrastructure.Delivery;

namespace Competition.Web;

/// <summary>
/// Outermost driver: polls pending events, delegates HTTP to a replaceable adapter,
/// and records each delivery outcome. Leases protect parallel processes.
/// </summary>
public sealed class OutboxDispatchWorker(
    IServiceScopeFactory scopes,
    IOutboxEventSink sink,
    ILogger<OutboxDispatchWorker> logger,
    TimeProvider clock) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private const int BatchSize = 5;
    private const int MaximumAttempts = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                // Exception messages may contain configuration or network details.
                logger.LogError("Outbox poll failed. FailureType={FailureType}", error.GetType().Name);
            }
            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    public async Task DispatchOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxDeliveryStore>();
        var jobs = await store.ClaimAsync(BatchSize, clock.GetUtcNow(), Lease, cancellationToken);
        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeliveryResult outcome;
            try { outcome = await sink.SendAsync(job, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                // Unexpected adapter failures are retryable; log only the exception type.
                logger.LogWarning("Outbox adapter failed. EventId={EventId}, FailureType={FailureType}",
                    job.Id, error.GetType().Name);
                outcome = DeliveryResult.RetryableFailure;
            }
            var recorded = outcome switch
            {
                DeliveryResult.Delivered =>
                    await store.MarkPublishedAsync(job.Id, job.LeaseToken, clock.GetUtcNow(), cancellationToken),
                DeliveryResult.PermanentFailure =>
                    await store.RecordFailureAsync(job.Id, job.LeaseToken, clock.GetUtcNow(),
                        "delivery.permanent", 1, cancellationToken),
                _ => await store.RecordFailureAsync(job.Id, job.LeaseToken, clock.GetUtcNow(),
                    "delivery.transient", MaximumAttempts, cancellationToken)
            };
            if (!recorded)
                logger.LogWarning("Outbox lease was lost. EventId={EventId}", job.Id);
            else
                logger.LogInformation("Outbox processed. EventId={EventId}, DeliveryOutcome={DeliveryOutcome}, Attempt={Attempt}",
                    job.Id, outcome, job.AttemptCount);
        }
    }
}
