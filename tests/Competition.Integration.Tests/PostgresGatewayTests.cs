using Competition.Application.Ports;
using Competition.Domain.Competitions;
using Competition.Domain.Results;
using Competition.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Competition.Integration.Tests;

/// <summary>Real PostgreSQL: no in-memory provider, mocked uniqueness or fake transactions.</summary>
public sealed class PostgresGatewayTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private DbContextOptions<CompetitionDbContext> _options = null!;
    private static readonly DateTimeOffset Now = new(2027, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        await _database.StartAsync();
        _options = new DbContextOptionsBuilder<CompetitionDbContext>()
            .UseNpgsql(_database.GetConnectionString()).Options;
        await using var context = Context();
        await context.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private CompetitionDbContext Context() => new(_options);

    [Fact]
    public async Task Accepting_once_commits_result_receipt_and_audit_atomically()
    {
        var contestId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        await using (var context = Context())
        {
            var competitions = new PostgresCompetitionGateway(context);
            Assert.True(await competitions.CreateAsync(Competition.Domain.Competitions.Competition.Schedule(
                contestId, "Cup", Now.AddDays(-1), Now.AddDays(2), 100), CancellationToken.None));
            var enrollments = new PostgresEnrollmentGateway(context);
            Assert.Equal(EnrollmentWriteStatus.Created,
                await enrollments.RegisterAsync(contestId, actorId, CancellationToken.None));
            Assert.Equal(EnrollmentWriteStatus.AlreadyExists,
                await enrollments.RegisterAsync(contestId, actorId, CancellationToken.None));
        }

        var receipt = VerifiedResult.Create(Guid.NewGuid(), contestId, actorId, 80, Now, "trusted-a");
        await using (var context = Context())
        {
            var result = await new PostgresResultGateway(context).AppendAsync(
                receipt, "same-request", "AABB", CancellationToken.None);
            Assert.Equal(ResultWriteStatus.Created, result.Status);
        }
        await using (var context = Context())
        {
            var result = await new PostgresResultGateway(context).AppendAsync(
                VerifiedResult.Create(Guid.NewGuid(), contestId, actorId, 80, Now, "trusted-a"),
                "same-request", "AABB", CancellationToken.None);
            Assert.Equal(ResultWriteStatus.Replayed, result.Status);
            Assert.Equal(receipt.Id, result.Result?.Id);
            Assert.Single(await context.Results.ToListAsync());
            Assert.Single(await context.RequestReceipts.ToListAsync());
            Assert.Single(await context.AuditEntries.ToListAsync());
            Assert.Single(await context.PendingEvents.ToListAsync());
        }
    }

    [Fact]
    public async Task Same_key_different_payload_and_same_evidence_new_key_are_conflicts()
    {
        var contestId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        await using (var context = Context())
        {
            var gateway = new PostgresResultGateway(context);
            var accepted = VerifiedResult.Create(Guid.NewGuid(), contestId, actorId, 42, Now, "proof-1");
            Assert.Equal(ResultWriteStatus.Created,
                (await gateway.AppendAsync(accepted, "request-123", "hash-one", CancellationToken.None)).Status);
        }
        await using (var context = Context())
        {
            var gateway = new PostgresResultGateway(context);
            var changed = VerifiedResult.Create(Guid.NewGuid(), contestId, actorId, 99, Now, "proof-2");
            Assert.Equal(ResultWriteStatus.Conflict,
                (await gateway.AppendAsync(changed, "request-123", "different-hash", CancellationToken.None)).Status);
            var replayProof = VerifiedResult.Create(Guid.NewGuid(), contestId, actorId, 42, Now, "proof-1");
            Assert.Equal(ResultWriteStatus.Conflict,
                (await gateway.AppendAsync(replayProof, "another-request", "hash-three", CancellationToken.None)).Status);
        }
        await using (var context = Context())
        {
            Assert.Single(await context.Results.ToListAsync());
            Assert.Single(await context.RequestReceipts.ToListAsync());
            Assert.Single(await context.AuditEntries.ToListAsync());
            Assert.Single(await context.PendingEvents.ToListAsync());
        }
    }

    [Fact]
    public async Task Concurrent_duplicate_submissions_have_only_one_committed_decision()
    {
        var contestId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        async Task<ResultWriteOutcome> Write()
        {
            await using var context = Context();
            var candidate = VerifiedResult.Create(Guid.NewGuid(), contestId, actorId, 12, Now, "proof-parallel");
            return await new PostgresResultGateway(context).AppendAsync(
                candidate, "parallel-key", "fingerprint", CancellationToken.None);
        }

        var completed = await Task.WhenAll(Task.Run(Write), Task.Run(Write));
        Assert.Single(completed, x => x.Status == ResultWriteStatus.Created);
        Assert.Single(completed, x => x.Status == ResultWriteStatus.Replayed);
        await using var verify = Context();
        Assert.Equal(1, await verify.Results.CountAsync());
        Assert.Equal(1, await verify.RequestReceipts.CountAsync());
        Assert.Equal(1, await verify.AuditEntries.CountAsync());
    }

    [Fact]
    public async Task Evidence_cannot_be_replayed_across_two_participants_in_same_competition()
    {
        var competitionId = Guid.NewGuid();
        await using (var context = Context())
        {
            var first = VerifiedResult.Create(Guid.NewGuid(), competitionId, Guid.NewGuid(), 20, Now, "unique-evidence");
            Assert.Equal(ResultWriteStatus.Created, (await new PostgresResultGateway(context).AppendAsync(
                first, "request-first", "first", CancellationToken.None)).Status);
        }
        await using (var context = Context())
        {
            var other = VerifiedResult.Create(Guid.NewGuid(), competitionId, Guid.NewGuid(), 20, Now, "unique-evidence");
            Assert.Equal(ResultWriteStatus.Conflict, (await new PostgresResultGateway(context).AppendAsync(
                other, "request-other", "second", CancellationToken.None)).Status);
        }
    }
    [Fact]
    public async Task Append_only_corrections_update_rankings_and_preserve_full_personal_history()
    {
        var competitionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var original = VerifiedResult.Create(Guid.NewGuid(), competitionId, participantId, 65,
            Now, "evidence-to-correct");
        await using (var context = Context())
        {
            Assert.Equal(ResultWriteStatus.Created, (await new PostgresResultGateway(context)
                .AppendAsync(original, "original-key", "original-fingerprint", CancellationToken.None)).Status);
        }
        await using (var context = Context())
        {
            var correction = new PostgresCorrectionGateway(context);
            Assert.Equal(new CorrectionOutcome(CorrectionStatus.Applied, 1), await correction.AppendAsync(
                competitionId, original.Id, Guid.NewGuid(), 0, 40,
                "Verified referee correction", Now.AddMinutes(1), CancellationToken.None));
            Assert.Equal(CorrectionStatus.VersionConflict, (await correction.AppendAsync(
                competitionId, original.Id, Guid.NewGuid(), 0, 90,
                "Stale competing correction", Now.AddMinutes(2), CancellationToken.None)).Status);
        }
        await using (var context = Context())
        {
            var corrections = new PostgresCorrectionGateway(context);
            Assert.Equal(new CorrectionOutcome(CorrectionStatus.Applied, 2), await corrections.AppendAsync(
                competitionId, original.Id, Guid.NewGuid(), 1, 55,
                "New referee evidence", Now.AddMinutes(3), CancellationToken.None));
        }
        await using (var context = Context())
        {
            var effective = await new PostgresResultGateway(context).ReadCompetitionAsync(
                competitionId, CancellationToken.None);
            Assert.Single(effective);
            Assert.Equal(55, effective[0].Points);
        }
        await using (var context = Context())
        {
            var history = await new PostgresParticipantHistoryGateway(context).ReadAsync(
                competitionId, participantId, CancellationToken.None);
            Assert.Equal(3, history.Count);
            Assert.Equal(new[] { 55, 40, 65 }, history.Select(x => x.Points));
            Assert.Equal(new[] { 2, 1, 0 }, history.Select(x => x.Revision));
            Assert.Equal(3, await context.AuditEntries.CountAsync());
            Assert.Equal(3, await context.PendingEvents.CountAsync());
            Assert.Equal(2, await context.Corrections.CountAsync());
            Assert.Equal(1, await context.Results.CountAsync());
        }
    }

    [Fact]
    public async Task Simultaneous_corrections_cannot_both_claim_the_same_revision()
    {
        var competitionId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var result = VerifiedResult.Create(Guid.NewGuid(), competitionId, participantId, 60,
            Now, "concurrent-correction-proof");
        await using (var context = Context())
        {
            Assert.Equal(ResultWriteStatus.Created, (await new PostgresResultGateway(context)
                .AppendAsync(result, "correction-base", "fingerprint-base", CancellationToken.None)).Status);
        }

        async Task<CorrectionOutcome> Correct(int points)
        {
            await using var context = Context();
            return await new PostgresCorrectionGateway(context).AppendAsync(
                competitionId, result.Id, Guid.NewGuid(), 0, points,
                "Independent referee verification", Now.AddMinutes(1), CancellationToken.None);
        }

        var outcomes = await Task.WhenAll(Task.Run(() => Correct(32)), Task.Run(() => Correct(47)));
        Assert.Single(outcomes, x => x.Status == CorrectionStatus.Applied);
        Assert.Single(outcomes, x => x.Status == CorrectionStatus.VersionConflict);

        await using var verified = Context();
        Assert.Equal(1, await verified.Corrections.CountAsync(x => x.ResultId == result.Id));
        Assert.Equal(2, await verified.AuditEntries.CountAsync(x => x.ResultId == result.Id));
        var latest = await new PostgresResultGateway(verified).ReadCompetitionAsync(competitionId, CancellationToken.None);
        Assert.Single(latest);
        Assert.Contains(latest[0].Points, new[] { 32, 47 });
        Assert.Equal(60, (await verified.Results.SingleAsync(x => x.Id == result.Id)).Points);
    }

    [Fact]
    public async Task Concurrent_proof_replay_with_distinct_request_keys_writes_only_once()
    {
        var competitionId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        async Task<ResultWriteOutcome> Write(string key)
        {
            await using var context = Context();
            var proposed = VerifiedResult.Create(Guid.NewGuid(), competitionId, actorId, 15,
                Now, "shared-proof-identity");
            return await new PostgresResultGateway(context).AppendAsync(
                proposed, key, key + "-fingerprint", CancellationToken.None);
        }

        var outcomes = await Task.WhenAll(Task.Run(() => Write("request-key-a")),
            Task.Run(() => Write("request-key-b")));
        Assert.Single(outcomes, x => x.Status == ResultWriteStatus.Created);
        Assert.Single(outcomes, x => x.Status == ResultWriteStatus.Conflict);
        await using var verified = Context();
        Assert.Equal(1, await verified.Results.CountAsync(x => x.CompetitionId == competitionId));
        Assert.Equal(1, await verified.RequestReceipts.CountAsync(x => x.CompetitionId == competitionId));
        Assert.Equal(1, await verified.AuditEntries.CountAsync(x => x.CompetitionId == competitionId));
    }

    [Fact]
    public async Task Corrections_do_not_cross_competition_or_participant_boundaries()
    {
        var one = Guid.NewGuid();
        var two = Guid.NewGuid();
        var player = Guid.NewGuid();
        var result = VerifiedResult.Create(Guid.NewGuid(), one, player, 12, Now, "proof-isolated");
        await using (var context = Context())
        {
            Assert.Equal(ResultWriteStatus.Created, (await new PostgresResultGateway(context)
                .AppendAsync(result, "isolation-request", "isolation-fingerprint", CancellationToken.None)).Status);
        }
        await using (var context = Context())
        {
            var correction = await new PostgresCorrectionGateway(context).AppendAsync(
                two, result.Id, Guid.NewGuid(), 0, 90, "Trying an invalid cross-competition correction",
                Now.AddMinutes(1), CancellationToken.None);
            Assert.Equal(CorrectionStatus.NotFound, correction.Status);
            Assert.Empty(await new PostgresParticipantHistoryGateway(context).ReadAsync(
                one, Guid.NewGuid(), CancellationToken.None));
            Assert.Empty(await new PostgresParticipantHistoryGateway(context).ReadAsync(
                two, player, CancellationToken.None));
            Assert.Equal(0, await context.Corrections.CountAsync());
        }
    }

}
