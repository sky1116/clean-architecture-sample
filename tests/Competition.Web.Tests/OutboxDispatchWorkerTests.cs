using Competition.Infrastructure.Delivery;
using Competition.Web;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Competition.Web.Tests;

public sealed class OutboxDispatchWorkerTests
{
    private sealed class Store : IOutboxDeliveryStore
    {
        private readonly OutboxDelivery _event = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "competition.result.accepted.v1", "{\"SchemaVersion\":1}", 1);
        public int Published { get; private set; }
        public int Failures { get; private set; }
        public string? LastReason { get; private set; }
        public int? LastMaxAttempts { get; private set; }
        public int Claims { get; private set; }

        public Task<IReadOnlyList<OutboxDelivery>> ClaimAsync(int limit, DateTimeOffset now,
            TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            Claims++;
            return Task.FromResult<IReadOnlyList<OutboxDelivery>>(Claims == 1 ? [_event] : []);
        }

        public Task<bool> MarkPublishedAsync(Guid id, Guid token, DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            Assert.Equal(_event.Id, id);
            Assert.Equal(_event.LeaseToken, token);
            Published++;
            return Task.FromResult(true);
        }

        public Task<bool> RecordFailureAsync(Guid id, Guid token, DateTimeOffset now,
            string safeReasonCode, int maxAttempts, CancellationToken cancellationToken)
        {
            Assert.Equal(_event.Id, id);
            Assert.Equal(_event.LeaseToken, token);
            Failures++;
            LastReason = safeReasonCode;
            LastMaxAttempts = maxAttempts;
            return Task.FromResult(true);
        }
    }

    private sealed class Sender(DeliveryResult status) : IOutboxEventSink
    {
        public int Calls { get; private set; }
        public Task<DeliveryResult> SendAsync(OutboxDelivery delivery, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(status);
        }
    }

    [Theory]
    [InlineData(DeliveryResult.Delivered, 1, 0, null, null)]
    [InlineData(DeliveryResult.RetryableFailure, 0, 1, "delivery.transient", 5)]
    [InlineData(DeliveryResult.PermanentFailure, 0, 1, "delivery.permanent", 1)]
    public async Task Each_delivery_outcome_is_durably_classified(
        DeliveryResult outcome, int expectedPublished, int expectedFailures,
        string? reason, int? maxAttempts)
    {
        var store = new Store();
        var sender = new Sender(outcome);
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IOutboxDeliveryStore>(store)
            .BuildServiceProvider();
        var worker = new OutboxDispatchWorker(services.GetRequiredService<IServiceScopeFactory>(),
            sender, services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<OutboxDispatchWorker>>(),
            TimeProvider.System);

        await worker.DispatchOnceAsync(CancellationToken.None);
        Assert.Equal(expectedPublished, store.Published);
        Assert.Equal(expectedFailures, store.Failures);
        Assert.Equal(reason, store.LastReason);
        Assert.Equal(maxAttempts, store.LastMaxAttempts);
        Assert.Equal(1, sender.Calls);
    }
}
