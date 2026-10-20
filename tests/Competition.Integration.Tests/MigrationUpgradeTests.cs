using Competition.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;
using Xunit;

namespace Competition.Integration.Tests;

/// <summary>Checks real migrations rather than simulating an empty schema with EnsureCreated.</summary>
public sealed class MigrationUpgradeTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private DbContextOptions<CompetitionDbContext> _options = null!;
    private CompetitionDbContext Open() => new(_options);

    public async Task InitializeAsync()
    {
        await _database.StartAsync();
        _options = new DbContextOptionsBuilder<CompetitionDbContext>()
            .UseNpgsql(_database.GetConnectionString()).Options;
    }

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Fact]
    public async Task Migrating_from_base_schema_to_outbox_preserves_existing_business_records()
    {
        var id = Guid.NewGuid();
        await using var context = Open();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20261020140100_BaseCompetition");
        var baseline = (await context.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.Single(baseline);
        Assert.Equal("20261020140100_BaseCompetition", baseline[0]);

        context.Competitions.Add(new CompetitionRow
        {
            Id = id, Name = "Keep historical records",
            StartsAt = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
            EndsAt = new DateTimeOffset(2027, 2, 1, 0, 0, 0, TimeSpan.Zero),
            MaximumPoints = 120
        });
        await context.SaveChangesAsync();

        await migrator.MigrateAsync();
        Assert.Equal(2, (await context.Database.GetAppliedMigrationsAsync()).Count());
        var persisted = await context.Competitions.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal("Keep historical records", persisted.Name);
        Assert.Empty(await context.PendingEvents.ToListAsync());

        // Reapplying to the latest target must be safe and preserve both migration records.
        await migrator.MigrateAsync();
        Assert.Equal(2, (await context.Database.GetAppliedMigrationsAsync()).Count());
        Assert.Equal(1, await context.Competitions.CountAsync());
    }

    [Fact]
    public async Task Fresh_database_migration_applies_all_known_versions()
    {
        await using var context = Open();
        await context.Database.MigrateAsync();
        Assert.Equal(new[]
        {
            "20261020140100_BaseCompetition",
            "20261020140200_DurableOutbox"
        }, (await context.Database.GetAppliedMigrationsAsync()).OrderBy(x => x).ToArray());
        Assert.Empty(await context.PendingEvents.ToArrayAsync());
    }
    [Fact]
    public void Checked_in_snapshot_must_match_the_current_EF_model()
    {
        using var context = Open();
        Assert.False(context.Database.HasPendingModelChanges(),
            "A domain/persistence model change requires a reviewed migration and an updated snapshot.");
    }

}
