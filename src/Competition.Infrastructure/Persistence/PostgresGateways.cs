using Competition.Application.Ports;
using System.Data;
using Competition.Domain;
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

public sealed class PostgresEnrollmentGateway(CompetitionDbContext database, IUtcClock clock) : IEnrollmentGateway
{
    public Task<bool> ExistsAsync(Guid competitionId, Guid participantId, CancellationToken cancellationToken) =>
        database.Enrollments.AsNoTracking().AnyAsync(x => x.CompetitionId == competitionId
            && x.ParticipantId == participantId, cancellationToken);

    public async Task<EnrollmentWriteStatus> RegisterAsync(Guid competitionId, Guid participantId,
        CancellationToken cancellationToken)
    {
        // Handle repeated registration within a single scoped DbContext before EF starts tracking
        // a second instance with the same composite primary key.
        // A previous successful registration remains idempotently visible after the cutoff.
        if (await ExistsAsync(competitionId, participantId, cancellationToken))
            return EnrollmentWriteStatus.AlreadyExists;

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var stored = await database.Competitions.AsNoTracking().SingleOrDefaultAsync(
            competition => competition.Id == competitionId, cancellationToken);
        if (stored is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return EnrollmentWriteStatus.CompetitionNotFound;
        }

        var policy = Competition.Domain.Competitions.Competition.Schedule(
            stored.Id, stored.Name, stored.StartsAt, stored.EndsAt, stored.MaximumPoints);
        try
        {
            policy.RequireRegistrationOpen(clock.UtcNow);
        }
        catch (DomainRuleException ex) when (ex.Code == "competition.registration_closed")
        {
            await transaction.RollbackAsync(cancellationToken);
            return EnrollmentWriteStatus.Closed;
        }

        database.Enrollments.Add(new EnrollmentRow { CompetitionId = competitionId, ParticipantId = participantId });
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return EnrollmentWriteStatus.Created;
        }
        catch (DbUpdateException exception) when (PostgresCompetitionGateway.IsUnique(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            database.ChangeTracker.Clear();
            return EnrollmentWriteStatus.AlreadyExists;
        }
    }
}

public sealed class PostgresResultGateway(CompetitionDbContext database, IUtcClock clock) : IResultGateway
{
    public async Task<ResultWriteOutcome?> FindExistingAsync(Guid competitionId,
        Guid participantId, string idempotencyKey, string requestFingerprint,
        CancellationToken cancellationToken)
    {
        var receipt = await database.RequestReceipts.AsNoTracking().SingleOrDefaultAsync(
            row => row.CompetitionId == competitionId && row.ParticipantId == participantId
                && row.IdempotencyKey == idempotencyKey, cancellationToken);
        return receipt is null
            ? null
            : await ExistingReceiptAsync(receipt, requestFingerprint, cancellationToken);
    }

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

        // This guard runs INSIDE the transaction, after any idempotent replay lookup.
        // Competition windows and accepted enrollments are immutable in this sample.
        // The clock is an inward-owned port to support deterministic adversarial tests.
        // Acceptance time is the in-transaction policy check, not the HTTP arrival time.
        var competition = await database.Competitions.AsNoTracking().SingleOrDefaultAsync(
            row => row.Id == proposed.CompetitionId, cancellationToken);
        if (competition is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(ResultWriteStatus.CompetitionNotFound, null);
        }
        // Reuse inner Entity policy, rather than copying business conditions into EF code.
        var competitionPolicy = Competition.Domain.Competitions.Competition.Schedule(
            competition.Id, competition.Name, competition.StartsAt, competition.EndsAt,
            competition.MaximumPoints);
        try
        {
            competitionPolicy.RequireResultWindow(clock.UtcNow);
            competitionPolicy.RequireValidPoints(proposed.Points);
        }
        catch (DomainRuleException ex) when (ex.Code is
            "competition.not_active" or "result.points_out_of_range")
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(ex.Code == "competition.not_active"
                ? ResultWriteStatus.CompetitionClosed : ResultWriteStatus.InvalidPoints, null);
        }
        if (!await database.Enrollments.AsNoTracking().AnyAsync(row =>
            row.CompetitionId == proposed.CompetitionId &&
            row.ParticipantId == proposed.ParticipantId, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(ResultWriteStatus.NotEnrolled, null);
        }
        var sameEvidence = await database.Results.AsNoTracking().AnyAsync(
            x => x.CompetitionId == proposed.CompetitionId && x.EvidenceId == proposed.EvidenceId,
            cancellationToken);
        if (sameEvidence)
        {
            await transaction.RollbackAsync(cancellationToken);
            // Another transaction may have committed the identical evidence
            // BETWEEN our first receipt lookup and this evidence lookup.
            // Resolve the committed receipt after rollback before deciding
            // that the request is a conflict. A matching key/fingerprint
            // must be classified as a harmless read-only replay.
            var winner = await database.RequestReceipts.AsNoTracking().SingleOrDefaultAsync(
                x => x.CompetitionId == proposed.CompetitionId &&
                    x.ParticipantId == proposed.ParticipantId &&
                    x.IdempotencyKey == idempotencyKey, cancellationToken);
            return winner is null
                ? new(ResultWriteStatus.Conflict, null)
                : await ExistingReceiptAsync(winner, requestFingerprint, cancellationToken);
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
        database.PendingEvents.Add(OutboxRecords.Accepted(proposed));

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
        await using var snapshot = await database.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        var rows = await database.Results.AsNoTracking().Where(x => x.CompetitionId == competitionId)
            .ToListAsync(cancellationToken);
        var revisions = await database.Corrections.AsNoTracking().Where(x => x.CompetitionId == competitionId)
            .ToListAsync(cancellationToken);
        var latest = revisions.GroupBy(x => x.ResultId).ToDictionary(x => x.Key,
            x => x.MaxBy(c => c.Revision)!);
        var effective = rows.Select(x => latest.TryGetValue(x.Id, out var revision)
            ? VerifiedResult.Create(x.Id, x.CompetitionId, x.ParticipantId, revision.ReplacementPoints,
                x.RecordedAt, x.EvidenceId)
            : ToDomain(x)).ToArray();
        await snapshot.CommitAsync(cancellationToken);
        return effective;
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
