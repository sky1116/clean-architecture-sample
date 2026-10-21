using System.Security.Cryptography;
using System.Text.Json;
using Competition.Application.UseCases;

namespace Competition.Adapters.Rest;

/// <summary>
/// Converts a versioned, authenticated transport envelope into plain Use Case input.
/// This adapter does not know ASP.NET Core or database models.
/// </summary>
public static class InboundEventAdapter
{
    public static ReceiveResultEventInput? Parse(ReadOnlySpan<byte> raw,
        Guid? expectedEventId = null, string? expectedType = null)
    {
        if (raw.IsEmpty || raw.Length > 8192) return null;
        try
        {
            using var document = JsonDocument.Parse(raw.ToArray(), new JsonDocumentOptions
            {
                MaxDepth = 8
            });
            var outer = document.RootElement;
            if (outer.ValueKind != JsonValueKind.Object ||
                !ReadGuid(outer, "EventId", out var eventId) ||
                !ReadGuid(outer, "CompetitionId", out var competitionId) ||
                !ReadString(outer, "EventType", out var eventType) ||
                !outer.TryGetProperty("Payload", out var payload) ||
                payload.ValueKind != JsonValueKind.Object ||
                !ReadInt(payload, "SchemaVersion", out var schemaVersion) ||
                schemaVersion != 1 ||
                !ReadGuid(payload, "CompetitionId", out var bodyCompetition) ||
                !ReadGuid(payload, "ResultId", out var resultId) ||
                competitionId != bodyCompetition ||
                (expectedEventId is not null && expectedEventId.Value != eventId) ||
                (expectedType is not null && expectedType != eventType))
                return null;

            Guid? participantId = null;
            int points;
            int revision;
            if (eventType == "competition.result.accepted.v1")
            {
                if (!ReadGuid(payload, "ParticipantId", out var participant) ||
                    !ReadInt(payload, "Points", out points))
                    return null;
                participantId = participant;
                revision = 0;
            }
            else if (eventType == "competition.result.corrected.v1")
            {
                if (!ReadInt(payload, "ReplacementPoints", out points) ||
                    !ReadInt(payload, "Revision", out revision))
                    return null;
            }
            else return null;

            return new ReceiveResultEventInput(
                eventId, competitionId, resultId, participantId, eventType, points,
                revision, Convert.ToHexString(SHA256.HashData(raw)));
        }
        catch (JsonException) { return null; }
    }

    private static bool ReadGuid(JsonElement source, string name, out Guid value)
    {
        value = Guid.Empty;
        return source.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            Guid.TryParse(property.GetString(), out value) && value != Guid.Empty;
    }

    private static bool ReadInt(JsonElement source, string name, out int value)
    {
        value = 0;
        return source.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out value);
    }

    private static bool ReadString(JsonElement source, string name, out string value)
    {
        value = "";
        if (!source.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(value) && value.Length <= 100;
    }
}
