using Competition.Application.Ports;
using Competition.Infrastructure.Delivery;
using Competition.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Competition.Integration.Tests;

/// <summary>
/// Tests real row locks, lease expiry, retry rules and terminal state using PostgreSQL.
/// No database simulator and no mock pretending to lock rows.
/// </summary>
public sealed class OutboxDeliveryTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private DbContextOptions<CompetitionDbContext> _options = null!;
    private static readonly DateTimeOffset Now = new(2027, 1, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    public async Task InitializeAsync()
    {
        await _database.StartAsync();
        _options = new DbContextOptionsBuilder<CompetitionDbContext>()
            .UseNpgsql(_database.GetConnectionString()).Options;
        await using var db = Open();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();
    private CompetitionDbContext Open() => new(_options);

    private async Task<Guid> SeedAsync(string type = "competition.result.accepted.v1")
    {
        var id = Guid.NewGuid();
        await using var db = Open();
        db.PendingEvents.Add(new OutboxRow
        {
            Id = id, CompetitionId = Guid.NewGuid(), EventType = type,
            Payload = "{\"SchemaVersion\":1}", OccurredAt = Now, NextAttemptAt = Now
        });
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task Published_event_is_not_claimed_again_and_wrong_lease_cannot_mark_it()
    {
        var id = await SeedAsync();
        await using var db = Open();
        var store = new PostgresOutboxDeliveryStore(db);
        var claimed = Assert.Single(await store.ClaimAsync(5, Now, Lease, CancellationToken.None));
        Assert.Equal(id, claimed.Id);
        Assert.Equal(1, claimed.AttemptCount);
        Assert.False(await store.MarkPublishedAsync(id, Guid.NewGuid(), Now.AddSeconds(1), CancellationToken.None));
        Assert.True(await store.MarkPublishedAsync(id, claimed.LeaseToken,
            Now.AddSeconds(1), CancellationToken.None));
        Assert.False(await store.MarkPublishedAsync(id, claimed.LeaseToken,
            Now.AddSeconds(2), CancellationToken.None));
        Assert.Empty(await store.ClaimAsync(5, Now.AddMinutes(4), Lease, CancellationToken.None));
        var stored = await db.PendingEvents.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.NotNull(stored.PublishedAt);
        Assert.Null(stored.LeaseToken);
    }

    [Fact]
    public async Task Failures_back_off_and_exhausted_records_enter_dead_letter()
    {
        var id = await SeedAsync();
        await using var db = Open();
        var store = new PostgresOutboxDeliveryStore(db);
        var first = Assert.Single(await store.ClaimAsync(1, Now, Lease, CancellationToken.None));
        Assert.True(await store.RecordFailureAsync(id, first.LeaseToken, Now.AddSeconds(1),
            "delivery.timeout", 2, CancellationToken.None));
        Assert.Empty(await store.ClaimAsync(1, Now.AddSeconds(2), Lease, CancellationToken.None));
        var second = Assert.Single(await store.ClaimAsync(1, Now.AddSeconds(4),
            Lease, CancellationToken.None));
        Assert.Equal(2, second.AttemptCount);
        Assert.True(await store.RecordFailureAsync(id, second.LeaseToken, Now.AddSeconds(5),
            "delivery.remote_error", 2, CancellationToken.None));
        Assert.Empty(await store.ClaimAsync(1, Now.AddHours(1), Lease, CancellationToken.None));
        var stored = await db.PendingEvents.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.NotNull(stored.DeadLetteredAt);
        Assert.Equal("delivery.remote_error", stored.LastFailureCode);
        Assert.Null(stored.PublishedAt);
        Assert.Equal(2, stored.AttemptCount);
    }

    [Fact]
    public async Task Lost_worker_lease_can_be_recovered_but_stale_worker_cannot_acknowledge()
    {
        var id = await SeedAsync();
        await using var firstDb = Open();
        var first = Assert.Single(await new PostgresOutboxDeliveryStore(firstDb)
            .ClaimAsync(1, Now, Lease, CancellationToken.None));
        await using var secondDb = Open();
        var secondStore = new PostgresOutboxDeliveryStore(secondDb);
        Assert.Empty(await secondStore.ClaimAsync(1, Now.AddMinutes(1), Lease, CancellationToken.None));
        var second = Assert.Single(await secondStore.ClaimAsync(1, Now.AddMinutes(3), Lease,
            CancellationToken.None));
        Assert.NotEqual(first.LeaseToken, second.LeaseToken);
        Assert.Equal(2, second.AttemptCount);
        Assert.False(await new PostgresOutboxDeliveryStore(firstDb).MarkPublishedAsync(
            id, first.LeaseToken, Now.AddMinutes(3), CancellationToken.None));
        Assert.True(await secondStore.MarkPublishedAsync(id, second.LeaseToken,
            Now.AddMinutes(3), CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_claims_of_two_events_do_not_overlap()
    {
        var firstId = await SeedAsync();
        var secondId = await SeedAsync();
        async Task<IReadOnlyList<OutboxDelivery>> Worker()
        {
            await using var db = Open();
            return await new PostgresOutboxDeliveryStore(db).ClaimAsync(
                1, Now, Lease, CancellationToken.None);
        }
        var claimed = await Task.WhenAll(Task.Run(Worker), Task.Run(Worker));
        var ids = claimed.SelectMany(x => x).Select(x => x.Id).ToArray();
        Assert.Equal(2, ids.Length);
        Assert.Equal(2, ids.Distinct().Count());
        Assert.Contains(firstId, ids);
        Assert.Contains(secondId, ids);
    }
    [Fact]
    public async Task Audited_operator_recovery_can_redrive_a_dead_letter_only_once()
    {
        var id = await SeedAsync();
        var actor = Guid.NewGuid();
        await using (var db = Open())
        {
            var delivery = new PostgresOutboxDeliveryStore(db);
            var claimed = Assert.Single(await delivery.ClaimAsync(1, Now, Lease, CancellationToken.None));
            Assert.True(await delivery.RecordFailureAsync(id, claimed.LeaseToken,
                Now.AddSeconds(1), "delivery.permanent", 1, CancellationToken.None));
        }

        await using (var db = Open())
        {
            var recovery = new PostgresOutboxRecoveryGateway(db);
            Assert.Equal(OutboxRecoveryStatus.Requeued, await recovery.RedriveAsync(
                id, actor, "Destination authentication was repaired", Now.AddMinutes(3),
                CancellationToken.None));
            Assert.Equal(OutboxRecoveryStatus.NotEligible, await recovery.RedriveAsync(
                id, actor, "Second unauthorized reset attempt", Now.AddMinutes(3),
                CancellationToken.None));
        }

        await using (var verify = Open())
        {
            var row = await verify.PendingEvents.AsNoTracking().SingleAsync(x => x.Id == id);
            Assert.Null(row.DeadLetteredAt);
            Assert.Null(row.LastFailureCode);
            Assert.Equal(0, row.AttemptCount);
            Assert.Equal(Now.AddMinutes(3), row.NextAttemptAt);
            var audit = Assert.Single(await verify.OutboxRecoveryAudits.AsNoTracking().ToListAsync());
            Assert.Equal(id, audit.EventId);
            Assert.Equal(actor, audit.AdministratorId);
            Assert.Equal(1, audit.PreviousAttempts);
            Assert.Equal("Destination authentication was repaired", audit.Reason);
        }

        await using (var db = Open())
        {
            var delivery = new PostgresOutboxDeliveryStore(db);
            var claimed = Assert.Single(await delivery.ClaimAsync(1, Now.AddMinutes(3),
                Lease, CancellationToken.None));
            Assert.Equal(id, claimed.Id);
            Assert.Equal(1, claimed.AttemptCount);
        }
    }

    [Fact]
    public async Task Published_event_cannot_be_requeued_as_a_dead_letter()
    {
        var id = await SeedAsync();
        await using (var db = Open())
        {
            var delivery = new PostgresOutboxDeliveryStore(db);
            var claimed = Assert.Single(await delivery.ClaimAsync(1, Now, Lease, CancellationToken.None));
            Assert.True(await delivery.MarkPublishedAsync(id, claimed.LeaseToken,
                Now.AddSeconds(1), CancellationToken.None));
        }

        await using (var db = Open())
        {
            Assert.Equal(OutboxRecoveryStatus.NotEligible,
                await new PostgresOutboxRecoveryGateway(db).RedriveAsync(
                    id, Guid.NewGuid(), "Must not redrive a published event", Now.AddMinutes(3),
                    CancellationToken.None));
            Assert.Empty(await db.OutboxRecoveryAudits.ToListAsync());
            Assert.NotNull((await db.PendingEvents.SingleAsync(x => x.Id == id)).PublishedAt);
        }
    }

    [Fact]
    public async Task Concurrent_operator_recovery_commits_one_audit_and_one_requeue()
    {
        var id = await SeedAsync();
        await using (var db = Open())
        {
            var store = new PostgresOutboxDeliveryStore(db);
            var claim = Assert.Single(await store.ClaimAsync(1, Now, Lease, CancellationToken.None));
            Assert.True(await store.RecordFailureAsync(id, claim.LeaseToken,
                Now.AddSeconds(1), "delivery.timeout", 1, CancellationToken.None));
        }

        async Task<OutboxRecoveryStatus> Redrive()
        {
            await using var db = Open();
            return await new PostgresOutboxRecoveryGateway(db).RedriveAsync(
                id, Guid.NewGuid(), "Operator inspected the failed endpoint",
                Now.AddMinutes(3), CancellationToken.None);
        }

        var results = await Task.WhenAll(Task.Run(Redrive), Task.Run(Redrive));
        Assert.Single(results, x => x == OutboxRecoveryStatus.Requeued);
        Assert.Single(results, x => x == OutboxRecoveryStatus.NotEligible);
        await using var verified = Open();
        Assert.Single(await verified.OutboxRecoveryAudits.ToListAsync());
        Assert.Null((await verified.PendingEvents.SingleAsync(x => x.Id == id)).DeadLetteredAt);
    }

}
