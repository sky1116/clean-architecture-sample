using Competition.Application.Ports;
using Competition.Application.UseCases;
using Competition.Infrastructure.Delivery;
using Competition.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Competition.Integration.Tests;

/// <summary>Real PostgreSQL receipt/projection atomicity, concurrency and restart tests.</summary>
public sealed class InboxGatewayTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private DbContextOptions<CompetitionDbContext> _options = null!;
    private static readonly DateTimeOffset Now = new(2027, 2, 1, 0, 0, 0, TimeSpan.Zero);
    private sealed class Clock : IUtcClock { public DateTimeOffset UtcNow => Now; }
    private CompetitionDbContext Open() => new(_options);
    private static ReceiveResultEventInput Accepted(Guid? eventId = null, Guid? competition = null,
        Guid? result = null, Guid? player = null) => new(
            eventId ?? Guid.NewGuid(), competition ?? Guid.NewGuid(),
            result ?? Guid.NewGuid(), player ?? Guid.NewGuid(),
            "competition.result.accepted.v1", 75, 0, new string('A', 64));

    private static ReceiveResultEventInput Corrected(ReceiveResultEventInput original,
        int revision, int points) => new(
            Guid.NewGuid(), original.CompetitionId, original.ResultId, null,
            "competition.result.corrected.v1", points, revision, new string('B', 64));

    public async Task InitializeAsync()
    {
        await _database.StartAsync();
        _options = new DbContextOptionsBuilder<CompetitionDbContext>()
            .UseNpgsql(_database.GetConnectionString()).Options;
        await using var database = Open();
        await database.Database.MigrateAsync();
    }
    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    private async Task<InboxWriteStatus> ReceiveAsync(ReceiveResultEventInput input)
    {
        // Each delivery opens a completely fresh context, like a separate receiver process.
        await using var context = Open();
        return await new PostgresResultInboxGateway(context, new Clock()).ApplyAsync(
            input, CancellationToken.None);
    }

    [Fact]
    public async Task Acknowledged_event_replayed_after_restart_applies_its_effect_once()
    {
        var accepted = Accepted();
        Assert.Equal(InboxWriteStatus.Applied, await ReceiveAsync(accepted));
        Assert.Equal(InboxWriteStatus.Duplicate, await ReceiveAsync(accepted));
        await using var verify = Open();
        var projection = Assert.Single(await verify.ReceivedResults.ToListAsync());
        Assert.Equal(75, projection.EffectivePoints);
        Assert.Equal(0, projection.Revision);
        Assert.Equal(accepted.ParticipantId, projection.ParticipantId);
        Assert.Single(await verify.InboxReceipts.ToListAsync());
        Assert.Empty(await verify.Results.ToListAsync());
    }

    [Fact]
    public async Task Same_id_with_modified_payload_is_a_conflict_not_an_idempotent_replay()
    {
        var accepted = Accepted();
        Assert.Equal(InboxWriteStatus.Applied, await ReceiveAsync(accepted));
        Assert.Equal(InboxWriteStatus.Conflict, await ReceiveAsync(
            accepted with { PayloadDigest = new string('B', 64) }));
        Assert.Equal(InboxWriteStatus.Conflict, await ReceiveAsync(
            accepted with { Points = 20, PayloadDigest = new string('C', 64) }));
        await using var db = Open();
        Assert.Single(await db.InboxReceipts.ToListAsync());
        Assert.Equal(75, (await db.ReceivedResults.SingleAsync()).EffectivePoints);
    }

    [Fact]
    public async Task Missing_dependency_is_retryable_without_poisoning_the_inbox()
    {
        var accepted = Accepted();
        var correction = Corrected(accepted, 1, 40);
        Assert.Equal(InboxWriteStatus.DependencyMissing, await ReceiveAsync(correction));
        await using (var verify = Open())
        {
            Assert.Empty(await verify.InboxReceipts.ToListAsync());
            Assert.Empty(await verify.ReceivedResults.ToListAsync());
        }
        Assert.Equal(InboxWriteStatus.Applied, await ReceiveAsync(accepted));
        Assert.Equal(InboxWriteStatus.Applied, await ReceiveAsync(correction));
        Assert.Equal(InboxWriteStatus.Duplicate, await ReceiveAsync(correction));
        await using var db = Open();
        Assert.Equal(2, await db.InboxReceipts.CountAsync());
        var row = await db.ReceivedResults.SingleAsync();
        Assert.Equal(40, row.EffectivePoints);
        Assert.Equal(1, row.Revision);
    }

    [Fact]
    public async Task Future_revision_waits_for_missing_intermediate_revision()
    {
        var accepted = Accepted();
        Assert.Equal(InboxWriteStatus.Applied, await ReceiveAsync(accepted));
        var future = Corrected(accepted, 2, 12);
        Assert.Equal(InboxWriteStatus.DependencyMissing, await ReceiveAsync(future));
        Assert.Equal(InboxWriteStatus.Applied, await ReceiveAsync(Corrected(accepted, 1, 30)));
        Assert.Equal(InboxWriteStatus.Applied, await ReceiveAsync(future));
        await using var db = Open();
        Assert.Equal(3, await db.InboxReceipts.CountAsync());
        Assert.Equal(12, (await db.ReceivedResults.SingleAsync()).EffectivePoints);
    }

    [Fact]
    public async Task Competing_receivers_can_only_commit_one_business_effect_for_an_event()
    {
        var accepted = Accepted();
        var outcomes = await Task.WhenAll(
            Task.Run(() => ReceiveAsync(accepted)),
            Task.Run(() => ReceiveAsync(accepted)));
        Assert.Single(outcomes, x => x == InboxWriteStatus.Applied);
        Assert.Single(outcomes, x => x == InboxWriteStatus.Duplicate);
        await using var db = Open();
        Assert.Single(await db.InboxReceipts.ToListAsync());
        Assert.Single(await db.ReceivedResults.ToListAsync());
    }

    [Fact]
    public async Task Repeated_parallel_same_event_deliveries_always_return_applied_then_duplicate()
    {
        // Repeated race probes use a fresh EventId each time. A single green
        // scheduling order is insufficient evidence for concurrency semantics.
        for (var iteration = 0; iteration < 8; iteration++)
        {
            var message = Accepted();
            var outcomes = await Task.WhenAll(
                Task.Run(() => ReceiveAsync(message)),
                Task.Run(() => ReceiveAsync(message)));
            Assert.Single(outcomes, x => x == InboxWriteStatus.Applied);
            Assert.Single(outcomes, x => x == InboxWriteStatus.Duplicate);
        }

        await using var verify = Open();
        Assert.Equal(8, await verify.InboxReceipts.CountAsync());
        Assert.Equal(8, await verify.ReceivedResults.CountAsync());
    }

    [Fact]
    public async Task Two_independent_corrections_claiming_one_revision_do_not_both_succeed()
    {
        var accepted = Accepted();
        Assert.Equal(InboxWriteStatus.Applied, await ReceiveAsync(accepted));
        var first = Corrected(accepted, 1, 35);
        var second = Corrected(accepted, 1, 50);
        var outcomes = await Task.WhenAll(Task.Run(() => ReceiveAsync(first)),
            Task.Run(() => ReceiveAsync(second)));
        Assert.Single(outcomes, x => x == InboxWriteStatus.Applied);
        Assert.Single(outcomes, x => x == InboxWriteStatus.Conflict);
        await using var db = Open();
        Assert.Equal(2, await db.InboxReceipts.CountAsync());
        var row = await db.ReceivedResults.SingleAsync();
        Assert.Equal(1, row.Revision);
        Assert.Contains(row.EffectivePoints, new[] { 35, 50 });
    }

    [Fact]
    public async Task A_distinct_event_must_not_replace_an_existing_result_identity()
    {
        var original = Accepted();
        Assert.Equal(InboxWriteStatus.Applied, await ReceiveAsync(original));
        Assert.Equal(InboxWriteStatus.Conflict, await ReceiveAsync(
            Accepted(competition: original.CompetitionId, result: original.ResultId)));
        await using var db = Open();
        Assert.Single(await db.InboxReceipts.ToListAsync());
        Assert.Single(await db.ReceivedResults.ToListAsync());
    }
}
