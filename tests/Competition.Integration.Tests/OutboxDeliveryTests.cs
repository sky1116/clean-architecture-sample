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
}
