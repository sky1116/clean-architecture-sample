namespace Competition.Domain.Competitions;

/// <summary>Application-independent competition rules and temporal invariants.</summary>
public sealed class Competition
{
    private Competition(Guid id, string name, DateTimeOffset startsAt, DateTimeOffset endsAt, int maximumPoints)
    {
        Id = id;
        Name = name;
        StartsAt = startsAt;
        EndsAt = endsAt;
        MaximumPoints = maximumPoints;
    }

    public Guid Id { get; }
    public string Name { get; }
    public DateTimeOffset StartsAt { get; }
    public DateTimeOffset EndsAt { get; }
    public int MaximumPoints { get; }

    public static Competition Schedule(Guid id, string name, DateTimeOffset startsAt, DateTimeOffset endsAt, int maximumPoints)
    {
        if (id == Guid.Empty) throw new DomainRuleException("competition.invalid_id");
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100)
            throw new DomainRuleException("competition.invalid_name");
        if (startsAt.Offset != TimeSpan.Zero || endsAt.Offset != TimeSpan.Zero || startsAt >= endsAt)
            throw new DomainRuleException("competition.invalid_window");
        if (maximumPoints is < 1 or > 1_000_000)
            throw new DomainRuleException("competition.invalid_maximum_points");
        return new Competition(id, name.Trim(), startsAt, endsAt, maximumPoints);
    }

    public void RequireRegistrationOpen(DateTimeOffset now)
    {
        if (now.Offset != TimeSpan.Zero || now >= StartsAt)
            throw new DomainRuleException("competition.registration_closed");
    }

    public void RequireResultWindow(DateTimeOffset now)
    {
        if (now.Offset != TimeSpan.Zero || now < StartsAt || now >= EndsAt)
            throw new DomainRuleException("competition.not_active");
    }

    public void RequireValidPoints(int points)
    {
        if (points < 0 || points > MaximumPoints)
            throw new DomainRuleException("result.points_out_of_range");
    }
}
