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
public sealed class HttpOutboxEventSink(HttpClient client, Uri destination) : IOutboxEventSink
{
    public async Task<DeliveryResult> SendAsync(OutboxDelivery delivery, CancellationToken cancellationToken)
    {
        if (destination.Scheme != Uri.UriSchemeHttps || !destination.IsAbsoluteUri
            || destination.UserInfo.Length != 0 || !string.IsNullOrEmpty(destination.Fragment))
            throw new InvalidOperationException("Outbox endpoint must be an absolute HTTPS URL without credentials or fragment.");

        using var request = new HttpRequestMessage(HttpMethod.Post, destination);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", delivery.Id.ToString("N"));
        request.Headers.TryAddWithoutValidation("X-Event-Id", delivery.Id.ToString("D"));
        request.Headers.TryAddWithoutValidation("X-Event-Type", delivery.EventType);
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            EventId = delivery.Id,
            delivery.CompetitionId,
            delivery.EventType,
            Payload = JsonDocument.Parse(delivery.Payload).RootElement
        }), Encoding.UTF8, "application/json");
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
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
