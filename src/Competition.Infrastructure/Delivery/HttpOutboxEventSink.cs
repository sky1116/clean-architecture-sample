using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Competition.Infrastructure.Delivery;

public enum DeliveryResult { Delivered, RetryableFailure, PermanentFailure }

/// <summary>Optional integration transport. The receiver MUST deduplicate by event ID.</summary>
public interface IOutboxEventSink
{
    Task<DeliveryResult> SendAsync(OutboxDelivery delivery, CancellationToken cancellationToken);
}

/// <summary>
/// Standard HTTP POST webhook, not a business rule. Destination comes from trusted server
/// configuration, never from a competition participant or the event payload.
/// </summary>
public sealed class HttpOutboxEventSink(HttpClient client, Uri destination, byte[] signingKey) : IOutboxEventSink
{
    public async Task<DeliveryResult> SendAsync(OutboxDelivery delivery, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (destination.Scheme != Uri.UriSchemeHttps || !destination.IsAbsoluteUri
            || destination.UserInfo.Length != 0 || !string.IsNullOrEmpty(destination.Fragment))
            throw new InvalidOperationException("Outbox endpoint must be an absolute HTTPS URL without credentials or fragment.");

        if (signingKey.Length < 32)
            throw new InvalidOperationException("Webhook signing requires at least 256 bits.");

        using var request = new HttpRequestMessage(HttpMethod.Post, destination);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", delivery.Id.ToString("N"));
        request.Headers.TryAddWithoutValidation("X-Event-Id", delivery.Id.ToString("D"));
        request.Headers.TryAddWithoutValidation("X-Event-Type", delivery.EventType);
        // Persisted events can be corrupted by operational mistakes or schema drift.
        // Unsupported/invalid messages are NOT transient network failures and must
        // never hit the remote endpoint or be retried as a temporary timeout.
        if (delivery.EventType is not ("competition.result.accepted.v1"
            or "competition.result.corrected.v1"))
            return DeliveryResult.PermanentFailure;

        byte[] bytes;
        try
        {
            using var payload = JsonDocument.Parse(delivery.Payload,
                new JsonDocumentOptions { MaxDepth = 8 });
            if (payload.RootElement.ValueKind != JsonValueKind.Object)
                return DeliveryResult.PermanentFailure;
            bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                EventId = delivery.Id,
                delivery.CompetitionId,
                delivery.EventType,
                Payload = payload.RootElement
            });
        }
        catch (JsonException) { return DeliveryResult.PermanentFailure; }

        // Inbound sample rejects messages exceeding this exact wire-size limit.
        if (bytes.Length > 8192)
            return DeliveryResult.PermanentFailure;
        // Sign the exact bytes actually sent, not the in-memory object.
        request.Headers.TryAddWithoutValidation("X-Webhook-Signature",
            WebhookSignature.Create(bytes, signingKey));
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            // Even test/custom handlers can ignore a caller's cancellation.
            // Never acknowledge remote delivery after the caller canceled.
            cancellationToken.ThrowIfCancellationRequested();
            if (response.IsSuccessStatusCode) return DeliveryResult.Delivered;
            if (response.StatusCode == HttpStatusCode.RequestTimeout || response.StatusCode == HttpStatusCode.TooManyRequests
                || (int)response.StatusCode >= 500)
                return DeliveryResult.RetryableFailure;
            return DeliveryResult.PermanentFailure;
        }
        catch (HttpRequestException)
        {
            return DeliveryResult.RetryableFailure;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DeliveryResult.RetryableFailure;
        }
    }
}
