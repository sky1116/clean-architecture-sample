namespace Competition.Domain.Results;

/// <summary>A verified result is immutable. Proof verification is a use-case boundary concern.</summary>
public sealed record VerifiedResult(
    Guid Id,
    Guid CompetitionId,
    Guid ParticipantId,
    int Points,
    DateTimeOffset RecordedAt,
    string EvidenceId)
{
    public static VerifiedResult Create(Guid id, Guid competitionId, Guid participantId,
        int points, DateTimeOffset recordedAt, string evidenceId)
    {
        if (id == Guid.Empty || competitionId == Guid.Empty || participantId == Guid.Empty)
            throw new DomainRuleException("result.invalid_identity");
        if (points < 0 || string.IsNullOrWhiteSpace(evidenceId) || evidenceId.Length > 128)
            throw new DomainRuleException("result.invalid_evidence");
        if (recordedAt.Offset != TimeSpan.Zero)
            throw new DomainRuleException("result.non_utc_timestamp");
        return new VerifiedResult(id, competitionId, participantId, points, recordedAt, evidenceId);
    }
}
