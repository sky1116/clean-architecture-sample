using Competition.Application.Ports;
using Competition.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Competition.Infrastructure.Delivery;

/// <summary>
/// Administrative redrive with a row lock and audit in one transaction.
/// Never mutate an already-published or currently leased event.
/// </summary>
public sealed class PostgresOutboxRecoveryGateway(CompetitionDbContext database)
    : IOutboxRecoveryPort
{
    public async Task<OutboxRecoveryStatus> RedriveAsync(Guid eventId, Guid administratorId,
        string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (eventId == Guid.Empty || administratorId == Guid.Empty ||
            now.Offset != TimeSpan.Zero || string.IsNullOrWhiteSpace(reason) ||
            reason.Trim().Length is < 8 or > 300)
            throw new ArgumentException("Invalid outbox recovery input.");

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var locked = await database.PendingEvents.FromSqlInterpolated(
            $"""SELECT * FROM "PendingEvents" WHERE "Id" = {eventId} FOR UPDATE""")
            .ToListAsync(cancellationToken);
        var eventRow = locked.SingleOrDefault();

        if (eventRow is null || eventRow.PublishedAt is not null ||
            eventRow.DeadLetteredAt is null ||
            eventRow.LeaseToken is not null || eventRow.LeaseUntil is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return OutboxRecoveryStatus.NotEligible;
        }

        var previous = eventRow.AttemptCount;
        eventRow.DeadLetteredAt = null;
        eventRow.LeaseUntil = null;
        eventRow.LeaseToken = null;
        eventRow.AttemptCount = 0;
        eventRow.NextAttemptAt = now;
        eventRow.LastFailureCode = null;
        database.OutboxRecoveryAudits.Add(new OutboxRecoveryAuditRow
        {
            Id = Guid.NewGuid(),
            EventId = eventId,
            AdministratorId = administratorId,
            Reason = reason.Trim(),
            PreviousAttempts = previous,
            OccurredAt = now
        });

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OutboxRecoveryStatus.Requeued;
    }
}
