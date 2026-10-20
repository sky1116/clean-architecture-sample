using Competition.Application.Ports;
using Competition.Application.UseCases;
using Competition.Infrastructure.Persistence;
using Competition.Domain.Results;
using Microsoft.EntityFrameworkCore;

namespace Competition.Infrastructure.Delivery;

/// <summary>
/// A consumer-side example: a durable EventId receipt and its observable projection
/// commit in the SAME PostgreSQL transaction. No fake process-local deduplication.
/// The consumer projection is separate from the authoritative result model.
/// </summary>
public sealed class PostgresResultInboxGateway(CompetitionDbContext database, IUtcClock clock)
    : IResultInboxPort
{
    public async Task<InboxWriteStatus> ApplyAsync(ReceiveResultEventInput input,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var old = await database.InboxReceipts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.EventId == input.EventId, cancellationToken);
        if (old is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return SameReceipt(old, input)
                ? InboxWriteStatus.Duplicate : InboxWriteStatus.Conflict;
        }

        var projection = await database.ReceivedResults.SingleOrDefaultAsync(
            x => x.CompetitionId == input.CompetitionId && x.ResultId == input.ResultId,
            cancellationToken);

        // All version-order and score policy comes from the inward Domain entity,
        // not from copied PostgreSQL checks.
        var current = projection is null ? null
            : new ReceivedResultPolicy.State(projection.ParticipantId,
                projection.EffectivePoints, projection.Revision);
        var decision = ReceivedResultPolicy.Decide(current,
            input.EventType == "competition.result.accepted.v1",
            input.ParticipantId, input.Points, input.Revision);
        if (decision.Kind != ReceivedResultPolicy.DecisionKind.Applied)
        {
            await transaction.RollbackAsync(cancellationToken);
            return decision.Kind == ReceivedResultPolicy.DecisionKind.DependencyMissing
                ? InboxWriteStatus.DependencyMissing : InboxWriteStatus.Conflict;
        }
        var next = decision.Next!;
        if (projection is null)
            database.ReceivedResults.Add(new ReceivedResultRow
            {
                CompetitionId = input.CompetitionId, ResultId = input.ResultId,
                ParticipantId = next.ParticipantId,
                EffectivePoints = next.EffectivePoints, Revision = next.Revision
            });
        else
        {
            projection.EffectivePoints = next.EffectivePoints;
            projection.Revision = next.Revision;
        }

        database.InboxReceipts.Add(new InboxReceiptRow
        {
            EventId = input.EventId, CompetitionId = input.CompetitionId,
            ResultId = input.ResultId, EventType = input.EventType,
            PayloadDigest = input.PayloadDigest, Revision = input.Revision,
            ReceivedAt = clock.UtcNow
        });

        try
        {
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return InboxWriteStatus.Applied;
        }
        catch (DbUpdateException error) when (PostgresCompetitionGateway.IsUnique(error))
        {
            await transaction.RollbackAsync(cancellationToken);
            database.ChangeTracker.Clear();

            var winner = await database.InboxReceipts.AsNoTracking()
                .SingleOrDefaultAsync(x => x.EventId == input.EventId, cancellationToken);
            if (winner is not null && SameReceipt(winner, input))
                return InboxWriteStatus.Duplicate;
            // Another event won the (ResultId, Revision) or projection uniqueness race.
            return InboxWriteStatus.Conflict;
        }
    }

    private static bool SameReceipt(InboxReceiptRow stored, ReceiveResultEventInput request) =>
        stored.CompetitionId == request.CompetitionId &&
        stored.ResultId == request.ResultId &&
        stored.Revision == request.Revision &&
        string.Equals(stored.EventType, request.EventType, StringComparison.Ordinal) &&
        string.Equals(stored.PayloadDigest, request.PayloadDigest, StringComparison.Ordinal);
}
