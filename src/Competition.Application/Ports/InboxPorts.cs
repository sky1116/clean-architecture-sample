using Competition.Application.UseCases;

namespace Competition.Application.Ports;

/// <summary>
/// The application owns the transaction contract. One accepted inbound EventId must
/// update its projection AND persist the receipt atomically, or neither happens.
/// </summary>
public interface IResultInboxPort
{
    Task<InboxWriteStatus> ApplyAsync(ReceiveResultEventInput input,
        CancellationToken cancellationToken);
}

public enum InboxWriteStatus { Applied, Duplicate, DependencyMissing, Conflict }
