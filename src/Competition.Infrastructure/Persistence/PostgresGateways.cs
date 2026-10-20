using Competition.Application.Ports;
using Competition.Domain.Competitions;
using Competition.Domain.Results;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Competition.Infrastructure.Persistence;

public sealed class PostgresCompetitionGateway(CompetitionDbContext database) : ICompetitionGateway
{
    public async Task<Competition.Domain.Competitions.Competition?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await database.Competitions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return row is null ? null : Competition.Domain.Competitions.Competition.Schedule(
            row.Id, row.Name, row.StartsAt, row.EndsAt, row.MaximumPoints);
    }

    public async Task<bool> CreateAsync(Competition.Domain.Competitions.Competition competition,
        CancellationToken cancellationToken)
    {
        database.Competitions.Add(new CompetitionRow {
            Id = competition.Id, Name = competition.Name, StartsAt = competition.StartsAt,
            EndsAt = competition.EndsAt, MaximumPoints = competition.MaximumPoints
        });
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (IsUnique(exception))
        {
            database.ChangeTracker.Clear();
            return false;
        }
    }

    internal static bool IsUnique(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}

public sealed class PostgresEnrollmentGateway(CompetitionDbContext database) : IEnrollmentGateway
{
    public Task<bool> ExistsAsync(Guid competitionId, Guid participantId, CancellationToken cancellationToken) =>
        database.Enrollments.AsNoTracking().AnyAsync(x => x.CompetitionId == competitionId
            && x.ParticipantId == participantId, cancellationToken);

    public async Task<EnrollmentWriteStatus> RegisterAsync(Guid competitionId, Guid participantId,
        CancellationToken cancellationToken)
    {
        database.Enrollments.Add(new EnrollmentRow { CompetitionId = competitionId, ParticipantId = participantId });
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return EnrollmentWriteStatus.Created;
        }
        catch (DbUpdateException exception) when (PostgresCompetitionGateway.IsUnique(exception))
        {
            database.ChangeTracker.Clear();
            return EnrollmentWriteStatus.AlreadyExists;
        }
    }
}

public sealed class PostgresResultGateway(CompetitionDbContext database) : IResultGateway
{
    public async Task<ResultWriteOutcome> AppendAsync(VerifiedResult proposed, string idempotencyKey,
        string requestFingerprint, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var existing = await database.RequestReceipts.AsNoTracking().SingleOrDefaultAsync(
            x => x.CompetitionId == proposed.CompetitionId && x.ParticipantId == proposed.ParticipantId
                && x.IdempotencyKey == idempotencyKey, cancellationToken);

        if (existing is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return await ExistingReceiptAsync(existing, requestFingerprint, cancellationToken);
        }

        var sameEvidence = await database.Results.AsNoTracking().AnyAsync(
            x => x.CompetitionId == proposed.CompetitionId && x.EvidenceId == proposed.EvidenceId,
            cancellationToken);
        if (sameEvidence)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(ResultWriteStatus.Conflict, null);
        }

        database.Results.Add(new ResultRow {
            Id = proposed.Id, CompetitionId = proposed.CompetitionId, ParticipantId = proposed.ParticipantId,
            Points = proposed.Points, EvidenceId = proposed.EvidenceId, RecordedAt = proposed.RecordedAt
        });
        database.RequestReceipts.Add(new RequestReceiptRow {
            CompetitionId = proposed.CompetitionId, ParticipantId = proposed.ParticipantId,
            IdempotencyKey = idempotencyKey, Fingerprint = requestFingerprint, ResultId = proposed.Id
        });
        // Audit is inserted in the same commit as the accepted result; not merely a log statement.
        database.AuditEntries.Add(new AuditRow {
            Id = Guid.NewGuid(), CompetitionId = proposed.CompetitionId, ParticipantId = proposed.ParticipantId,
            ResultId = proposed.Id, Action = "ResultAccepted", OccurredAt = proposed.RecordedAt
        });

        try
        {
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(ResultWriteStatus.Created, proposed);
        }
        catch (DbUpdateException exception) when (PostgresCompetitionGateway.IsUnique(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            database.ChangeTracker.Clear();
            // A concurrent writer won: classify replay vs conflict using the committed unique receipt.
            var winner = await database.RequestReceipts.AsNoTracking().SingleOrDefaultAsync(
                x => x.CompetitionId == proposed.CompetitionId && x.ParticipantId == proposed.ParticipantId
                    && x.IdempotencyKey == idempotencyKey, cancellationToken);
            return winner is null
                ? new(ResultWriteStatus.Conflict, null)
                : await ExistingReceiptAsync(winner, requestFingerprint, cancellationToken);
        }
    }

    private async Task<ResultWriteOutcome> ExistingReceiptAsync(RequestReceiptRow receipt,
        string fingerprint, CancellationToken cancellationToken)
    {
        if (!string.Equals(receipt.Fingerprint, fingerprint, StringComparison.Ordinal))
            return new(ResultWriteStatus.Conflict, null);
        var row = await database.Results.AsNoTracking().SingleAsync(x => x.Id == receipt.ResultId,
            cancellationToken);
        return new(ResultWriteStatus.Replayed, ToDomain(row));
    }

    public async Task<IReadOnlyList<VerifiedResult>> ReadCompetitionAsync(Guid competitionId,
        CancellationToken cancellationToken)
    {
        var rows = await database.Results.AsNoTracking().Where(x => x.CompetitionId == competitionId)
            .ToListAsync(cancellationToken);
        return rows.Select(ToDomain).ToArray();
    }

    public async Task<IReadOnlyList<VerifiedResult>> ReadParticipantAsync(Guid competitionId, Guid participantId,
        CancellationToken cancellationToken)
    {
        var rows = await database.Results.AsNoTracking().Where(x => x.CompetitionId == competitionId
            && x.ParticipantId == participantId).ToListAsync(cancellationToken);
        return rows.Select(ToDomain).ToArray();
    }

    private static VerifiedResult ToDomain(ResultRow row) => VerifiedResult.Create(
        row.Id, row.CompetitionId, row.ParticipantId, row.Points, row.RecordedAt, row.EvidenceId);
}
