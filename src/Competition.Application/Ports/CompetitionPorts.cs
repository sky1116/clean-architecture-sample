using Competition.Domain.Competitions;
using Competition.Domain.Results;

namespace Competition.Application.Ports;

/// <summary>Time is a dependency so boundary and expiry tests can use a controlled clock.</summary>
public interface IUtcClock { DateTimeOffset UtcNow { get; } }
public interface IResultUuidSource { Guid Next() ; }

public interface ICompetitionGateway
{
    Task<Competition.Domain.Competitions.Competition?> FindAsync(Guid id, CancellationToken cancellationToken);
    Task<bool> CreateAsync(Competition.Domain.Competitions.Competition competition, CancellationToken cancellationToken);
}

public enum EnrollmentWriteStatus { Created, AlreadyExists }
public interface IEnrollmentGateway
{
    Task<bool> ExistsAsync(Guid competitionId, Guid participantId, CancellationToken cancellationToken);
    Task<EnrollmentWriteStatus> RegisterAsync(Guid competitionId, Guid participantId, CancellationToken cancellationToken);
}

public sealed record VerifiedEvidence(int Points, string EvidenceId);

/// <summary>A trusted system integration, NEVER a wrapper around a client-supplied score.</summary>
public interface IResultEvidencePort
{
    Task<VerifiedEvidence?> VerifyAsync(Guid competitionId, Guid participantId, string opaqueEvidence,
        CancellationToken cancellationToken);
}

public enum ResultWriteStatus { Created, Replayed, Conflict, CompetitionNotFound, CompetitionClosed, NotEnrolled, InvalidPoints }
public sealed record ResultWriteOutcome(ResultWriteStatus Status, VerifiedResult? Result);

public interface IResultGateway
{
    /// <summary>Atomic acceptance, idempotency receipt and append-only audit record in one transaction.</summary>
    Task<ResultWriteOutcome> AppendAsync(VerifiedResult proposed, string idempotencyKey,
        string requestFingerprint, CancellationToken cancellationToken);
    Task<IReadOnlyList<VerifiedResult>> ReadCompetitionAsync(Guid competitionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<VerifiedResult>> ReadParticipantAsync(Guid competitionId, Guid participantId,
        CancellationToken cancellationToken);
}
