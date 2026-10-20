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
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine").Build();
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
}
