using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Competition.Application.Ports;

namespace Competition.Infrastructure.Security;

/// <summary>
/// Demonstration of server-to-server trusted receipts; the signing key MUST NEVER be a player/client secret.
/// Signature alone proves receipt origin, not that the issuing authority actually evaluated game outcomes.
/// </summary>
public sealed class HmacResultEvidencePort(byte[] trustedAuthorityKey, IUtcClock clock) : IResultEvidencePort
{
    private sealed record AuthorityReceipt(Guid CompetitionId, Guid ParticipantId,
        int Points, string EvidenceId, long IssuedAtUnix, long ExpiresAtUnix);

    public Task<VerifiedEvidence?> VerifyAsync(Guid competitionId, Guid participantId, string opaqueEvidence,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (trustedAuthorityKey.Length < 32 || string.IsNullOrWhiteSpace(opaqueEvidence)
            || opaqueEvidence.Length > 4096)
            return Task.FromResult<VerifiedEvidence?>(null);
        var parts = opaqueEvidence.Split('.');
        if (parts.Length != 2 || parts.Any(x => x.Length == 0 || x.Length > 3072))
            return Task.FromResult<VerifiedEvidence?>(null);
        try
        {
            var supplied = Decode(parts[1]);
            var expected = HMACSHA256.HashData(trustedAuthorityKey, Encoding.ASCII.GetBytes(parts[0]));
            if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected))
                return Task.FromResult<VerifiedEvidence?>(null);
            var payload = JsonSerializer.Deserialize<AuthorityReceipt>(Decode(parts[0]));
            var now = clock.UtcNow.ToUnixTimeSeconds();
            if (payload is null || payload.CompetitionId != competitionId
                || payload.ParticipantId != participantId || payload.IssuedAtUnix > now
                || payload.ExpiresAtUnix <= now || payload.ExpiresAtUnix - payload.IssuedAtUnix > 86400
                || payload.Points < 0 || string.IsNullOrWhiteSpace(payload.EvidenceId)
                || payload.EvidenceId.Length > 128)
                return Task.FromResult<VerifiedEvidence?>(null);
            return Task.FromResult<VerifiedEvidence?>(new(payload.Points, payload.EvidenceId));
        }
        catch (Exception exception) when (exception is FormatException or JsonException
            or ArgumentException or OverflowException)
        {
            return Task.FromResult<VerifiedEvidence?>(null);
        }
    }

    private static byte[] Decode(string input)
    {
        var base64 = input.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '='));
    }
}

public sealed class SystemUtcClock : IUtcClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
public sealed class RandomUuidSource : IResultUuidSource { public Guid Next() => Guid.NewGuid(); }
