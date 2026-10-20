namespace Competition.Application.Ports;

/// <summary>Corrections are append-only decisions. A result's originally accepted score is never overwritten.</summary>
public sealed record HistoricalDecision(Guid EventId, Guid ResultId, string Action,
    int Points, int Revision, DateTimeOffset OccurredAt, string? Reason);

public interface IParticipantHistoryPort
{
    Task<IReadOnlyList<HistoricalDecision>> ReadAsync(Guid competitionId, Guid participantId,
        CancellationToken cancellationToken);
}

public enum CorrectionStatus { Applied, NotFound, VersionConflict }
public sealed record CorrectionOutcome(CorrectionStatus Status, int? Revision);

public interface IResultCorrectionPort
{
    Task<CorrectionOutcome> AppendAsync(Guid competitionId, Guid resultId, Guid administratorId,
        int expectedRevision, int correctedPoints, string reason, DateTimeOffset now,
        CancellationToken cancellationToken);
}
