using System.Net;
using Competition.Infrastructure.Delivery;
using Xunit;

namespace Competition.Infrastructure.Tests;

public sealed class OutboxHttpSinkTests
{
    private static readonly byte[] SigningKey = Enumerable.Repeat((byte)37, 32).ToArray();
    private sealed class CaptureHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public string? EventId { get; private set; }
        public bool ValidSignature { get; private set; }
        public string? IdempotencyKey { get; private set; }
        public bool RedirectRequested { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var raw = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            Body = System.Text.Encoding.UTF8.GetString(raw);
            ValidSignature = WebhookSignature.Verify(raw,
                request.Headers.GetValues("X-Webhook-Signature").Single(), SigningKey);
            EventId = request.Headers.GetValues("X-Event-Id").Single();
            IdempotencyKey = request.Headers.GetValues("Idempotency-Key").Single();
            RedirectRequested = request.RequestUri?.Host != "event-receiver.example";
            return new HttpResponseMessage(status);
        }
    }

    private static OutboxDelivery Delivery() => new(Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), "competition.result.accepted.v1",
        "{\"SchemaVersion\":1,\"Points\":42}", 1);

    [Fact]
    public async Task Successful_webhook_includes_stable_event_identity_for_consumer_deduplication()
    {
        var handler = new CaptureHandler(HttpStatusCode.Accepted);
        using var client = new HttpClient(handler);
        var sender = new HttpOutboxEventSink(client, new Uri("https://event-receiver.example/events"), SigningKey);
        var delivery = Delivery();
        var outcome = await sender.SendAsync(delivery, CancellationToken.None);

        Assert.Equal(DeliveryResult.Delivered, outcome);
        Assert.True(handler.ValidSignature);
        Assert.Equal(delivery.Id.ToString("D"), handler.EventId);
        Assert.Equal(delivery.Id.ToString("N"), handler.IdempotencyKey);
        Assert.Contains(delivery.EventType, handler.Body);
        Assert.Contains("42", handler.Body);
        Assert.False(handler.RedirectRequested);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, DeliveryResult.RetryableFailure)]
    [InlineData(HttpStatusCode.RequestTimeout, DeliveryResult.RetryableFailure)]
    [InlineData(HttpStatusCode.ServiceUnavailable, DeliveryResult.RetryableFailure)]
    [InlineData(HttpStatusCode.Unauthorized, DeliveryResult.PermanentFailure)]
    [InlineData(HttpStatusCode.BadRequest, DeliveryResult.PermanentFailure)]
    public async Task Only_known_transient_http_responses_are_retried(
        HttpStatusCode status, DeliveryResult expected)
    {
        using var client = new HttpClient(new CaptureHandler(status));
        var sender = new HttpOutboxEventSink(client, new Uri("https://event-receiver.example/events"), SigningKey);
        Assert.Equal(expected, await sender.SendAsync(Delivery(), CancellationToken.None));
    }

    [Fact]
    public async Task Plaintext_http_target_is_rejected_before_any_request_is_sent()
    {
        using var client = new HttpClient(new CaptureHandler(HttpStatusCode.Accepted));
        var sender = new HttpOutboxEventSink(client, new Uri("http://event-receiver.example/events"), SigningKey);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sender.SendAsync(Delivery(), CancellationToken.None));
    }
}
