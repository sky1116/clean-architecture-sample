using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Competition.Application.Ports;
using Competition.Infrastructure.Security;
using Xunit;

namespace Competition.Infrastructure.Tests;

public sealed class EvidenceVerifierTests
{
    private sealed class Clock : IUtcClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    }
    private static readonly byte[] SigningKey = Enumerable.Repeat((byte)42, 32).ToArray();

    private static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Issue(Guid competitionId, Guid participantId, int points, string evidenceId,
        long issued = 1_799_999_990, long expires = 1_800_000_060)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new {
            CompetitionId = competitionId, ParticipantId = participantId, Points = points,
            EvidenceId = evidenceId, IssuedAtUnix = issued, ExpiresAtUnix = expires
        });
        var payload = Encode(body);
        var signature = HMACSHA256.HashData(SigningKey, Encoding.ASCII.GetBytes(payload));
        return payload + "." + Encode(signature);
    }

    [Fact]
    public async Task Authentic_bound_receipt_can_be_verified()
    {
        var competitionId = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var proof = Issue(competitionId, actor, 85, "event-123");
        var verifier = new HmacResultEvidencePort(SigningKey, new Clock());
        var result = await verifier.VerifyAsync(competitionId, actor, proof, CancellationToken.None);
        Assert.Equal(85, result?.Points);
        Assert.Equal("event-123", result?.EvidenceId);
    }

    [Fact]
    public async Task Proof_is_bound_to_actual_actor_and_competition()
    {
        var competition = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var verifier = new HmacResultEvidencePort(SigningKey, new Clock());
        var proof = Issue(competition, actor, 85, "event-123");
        Assert.Null(await verifier.VerifyAsync(competition, Guid.NewGuid(), proof, CancellationToken.None));
        Assert.Null(await verifier.VerifyAsync(Guid.NewGuid(), actor, proof, CancellationToken.None));
    }

    [Fact]
    public async Task Forgery_expiration_and_overlong_lifetime_are_rejected()
    {
        var competition = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var verifier = new HmacResultEvidencePort(SigningKey, new Clock());
        var valid = Issue(competition, actor, 50, "event-2");
        Assert.Null(await verifier.VerifyAsync(competition, actor, valid + "altered", CancellationToken.None));
        Assert.Null(await verifier.VerifyAsync(competition, actor,
            Issue(competition, actor, 50, "event-3", expires: 1_799_999_999), CancellationToken.None));
        Assert.Null(await verifier.VerifyAsync(competition, actor,
            Issue(competition, actor, 50, "event-4", issued: 1_700_000_000), CancellationToken.None));
    }
}
