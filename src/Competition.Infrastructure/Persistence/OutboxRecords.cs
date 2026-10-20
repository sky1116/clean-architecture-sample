using System.Text.Json;
using Competition.Domain.Results;

namespace Competition.Infrastructure.Persistence;

/// <summary>
/// Serialized integration event records are written in the SAME transaction as the
/// authoritative state. Persisted does not mean delivered; a separate dispatcher
/// is required before claiming any external publication guarantee.
/// </summary>
internal static class OutboxRecords
{
    public static OutboxRow Accepted(VerifiedResult result) => new()
    {
        Id = Guid.NewGuid(),
        CompetitionId = result.CompetitionId,
        EventType = "competition.result.accepted.v1",
        OccurredAt = result.RecordedAt,
        Payload = JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            result.CompetitionId,
            ResultId = result.Id,
            result.ParticipantId,
            result.Points
        })
    };

    public static OutboxRow Corrected(Guid competitionId, Guid resultId,
        int revision, int replacementPoints, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        CompetitionId = competitionId,
        EventType = "competition.result.corrected.v1",
        OccurredAt = now,
        Payload = JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            CompetitionId = competitionId,
            ResultId = resultId,
            Revision = revision,
            ReplacementPoints = replacementPoints
        })
    };
}
