using Competition.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Competition.Infrastructure.Delivery;

public sealed record OutboxDelivery(Guid Id, Guid LeaseToken, Guid CompetitionId,
    string EventType, string Payload, int AttemptCount);

/// <summary>
/// Mechanism-layer contract: outbox delivery is transport infrastructure, not a business
/// policy. A successful send without a subsequent commit MAY be sent again.
/// </summary>
public interface IOutboxDeliveryStore
{
    Task<IReadOnlyList<OutboxDelivery>> ClaimAsync(int limit, DateTimeOffset now,
        TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task<bool> MarkPublishedAsync(Guid id, Guid leaseToken, DateTimeOffset now,
        CancellationToken cancellationToken);
    Task<bool> RecordFailureAsync(Guid id, Guid leaseToken, DateTimeOffset now,
        string safeReasonCode, int maxAttempts, CancellationToken cancellationToken);
}

public sealed class PostgresOutboxDeliveryStore(CompetitionDbContext database) : IOutboxDeliveryStore
{
    public async Task<IReadOnlyList<OutboxDelivery>> ClaimAsync(int limit, DateTimeOffset now,
        TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100 || leaseDuration < TimeSpan.FromSeconds(30)
            || leaseDuration > TimeSpan.FromMinutes(15) || now.Offset != TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(limit), "Invalid claim bound.");
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);

        // The row locks last only until the lease is recorded and transaction commits.
        // Concurrent workers skip locked rows instead of selecting the same event.
        var rows = await database.PendingEvents.FromSqlInterpolated(
            $"""
             SELECT * FROM "PendingEvents"
             WHERE "PublishedAt" IS NULL
               AND "DeadLetteredAt" IS NULL
               AND "NextAttemptAt" <= {now}
               AND ("LeaseUntil" IS NULL OR "LeaseUntil" <= {now})
             ORDER BY "OccurredAt", "Id"
             LIMIT {limit}
             FOR UPDATE SKIP LOCKED
             """).ToListAsync(cancellationToken);

        var deliveries = new List<OutboxDelivery>(rows.Count);
        foreach (var row in rows)
        {
            var token = Guid.NewGuid();
            row.LeaseToken = token;
            row.LeaseUntil = now.Add(leaseDuration);
            row.AttemptCount = checked(row.AttemptCount + 1);
            deliveries.Add(new OutboxDelivery(
                row.Id, token, row.CompetitionId, row.EventType, row.Payload, row.AttemptCount));
        }

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        database.ChangeTracker.Clear();
        return deliveries;
    }

    public Task<bool> MarkPublishedAsync(Guid id, Guid leaseToken, DateTimeOffset now,
        CancellationToken cancellationToken) =>
        UpdateSuccessAsync(id, leaseToken, now, cancellationToken);

    private async Task<bool> UpdateSuccessAsync(Guid id, Guid leaseToken,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var affected = await database.PendingEvents.Where(row => row.Id == id
                && row.LeaseToken == leaseToken && row.PublishedAt == null
                && row.DeadLetteredAt == null && row.LeaseUntil > now)
            .ExecuteUpdateAsync(update => update
                .SetProperty(row => row.PublishedAt, (DateTimeOffset?)now)
                .SetProperty(row => row.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(row => row.LeaseToken, (Guid?)null)
                .SetProperty(row => row.LastFailureCode, (string?)null), cancellationToken);
        return affected == 1;
    }

    public async Task<bool> RecordFailureAsync(Guid id, Guid leaseToken, DateTimeOffset now,
        string safeReasonCode, int maxAttempts, CancellationToken cancellationToken)
    {
        if (maxAttempts is < 1 or > 20 || safeReasonCode.Length is < 1 or > 80
            || !safeReasonCode.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.'))
            throw new ArgumentOutOfRangeException(nameof(safeReasonCode));

        var row = await database.PendingEvents.AsNoTracking().SingleOrDefaultAsync(
            value => value.Id == id && value.LeaseToken == leaseToken
                && value.PublishedAt == null && value.DeadLetteredAt == null
                && value.LeaseUntil > now, cancellationToken);
        if (row is null) return false;
        var exhausted = row.AttemptCount >= maxAttempts;
        var retryDelay = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(8, row.AttemptCount))));
        var affected = await database.PendingEvents.Where(value => value.Id == id
                && value.LeaseToken == leaseToken && value.PublishedAt == null
                && value.DeadLetteredAt == null && value.LeaseUntil > now)
            .ExecuteUpdateAsync(update => update
                .SetProperty(value => value.LeaseToken, (Guid?)null)
                .SetProperty(value => value.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(value => value.NextAttemptAt, now.Add(retryDelay))
                .SetProperty(value => value.DeadLetteredAt, exhausted ? (DateTimeOffset?)now : null)
                .SetProperty(value => value.LastFailureCode, safeReasonCode), cancellationToken);
        return affected == 1;
    }
}
