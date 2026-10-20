using System.Data;
using Competition.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace Competition.Infrastructure.Persistence;

/// <summary>Append-only corrections with optimistic revision checking and atomic audit writes.</summary>
public sealed class PostgresCorrectionGateway(CompetitionDbContext database) : IResultCorrectionPort
{
    public async Task<CorrectionOutcome> AppendAsync(Guid competitionId, Guid resultId, Guid administratorId,
        int expectedRevision, int correctedPoints, string reason, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var originalExists = await database.Results.AsNoTracking().AnyAsync(
            x => x.Id == resultId && x.CompetitionId == competitionId, cancellationToken);
        if (!originalExists)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(CorrectionStatus.NotFound, null);
        }
        var revisions = await database.Corrections.AsNoTracking().Where(x => x.ResultId == resultId)
            .Select(x => x.Revision).ToListAsync(cancellationToken);
        var latest = revisions.Count == 0 ? 0 : revisions.Max();
        if (latest != expectedRevision)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(CorrectionStatus.VersionConflict, latest);
        }
        var next = checked(latest + 1);
        database.Corrections.Add(new CorrectionRow
        {
            Id = Guid.NewGuid(), CompetitionId = competitionId, ResultId = resultId,
            AdministratorId = administratorId, Revision = next,
            ReplacementPoints = correctedPoints, Reason = reason, OccurredAt = now
        });
        database.AuditEntries.Add(new AuditRow
        {
            Id = Guid.NewGuid(), CompetitionId = competitionId, ParticipantId = administratorId,
            ResultId = resultId, Action = "ResultCorrected", OccurredAt = now
        });
        database.PendingEvents.Add(OutboxRecords.Corrected(competitionId, resultId,
            next, correctedPoints, now));
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(CorrectionStatus.Applied, next);
        }
        catch (DbUpdateException exception) when (PostgresCompetitionGateway.IsUnique(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            database.ChangeTracker.Clear();
            return new(CorrectionStatus.VersionConflict, null);
        }
    }
}

/// <summary>The original accepted result and each correction remain independently discoverable.</summary>
public sealed class PostgresParticipantHistoryGateway(CompetitionDbContext database) : IParticipantHistoryPort
{
    public async Task<IReadOnlyList<HistoricalDecision>> ReadAsync(Guid competitionId, Guid participantId,
        CancellationToken cancellationToken)
    {
        await using var snapshot = await database.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        var originals = await database.Results.AsNoTracking().Where(x =>
            x.CompetitionId == competitionId && x.ParticipantId == participantId)
            .ToListAsync(cancellationToken);
        var ids = originals.Select(x => x.Id).ToArray();
        var corrections = await database.Corrections.AsNoTracking().Where(x =>
            x.CompetitionId == competitionId && ids.Contains(x.ResultId))
            .ToListAsync(cancellationToken);
        var events = originals.Select(x => new HistoricalDecision(
            x.Id, x.Id, "ResultAccepted", x.Points, 0, x.RecordedAt, null))
            .Concat(corrections.Select(x => new HistoricalDecision(
                x.Id, x.ResultId, "ResultCorrected", x.ReplacementPoints, x.Revision,
                x.OccurredAt, x.Reason)))
            .OrderByDescending(x => x.OccurredAt).ThenBy(x => x.EventId).ToArray();
        await snapshot.CommitAsync(cancellationToken);
        return events;
    }
}
