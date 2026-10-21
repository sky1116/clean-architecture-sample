namespace Competition.Application.Ports;

/// <summary>
/// Application-controlled operator action; infrastructure implements the atomic
/// redrive and durable audit. Ordinary HTTP requests cannot manipulate delivery rows.
/// </summary>
public interface IOutboxRecoveryPort
{
    Task<OutboxRecoveryStatus> RedriveAsync(Guid eventId, Guid administratorId,
        string reason, DateTimeOffset now, CancellationToken cancellationToken);
}

public enum OutboxRecoveryStatus { Requeued, NotEligible }
