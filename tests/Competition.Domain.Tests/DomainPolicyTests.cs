using Competition.Domain;
using Competition.Domain.Competitions;
using Competition.Domain.Rankings;
using Competition.Domain.Results;
using Xunit;

namespace Competition.Domain.Tests;

public sealed class DomainPolicyTests
{
    private static readonly DateTimeOffset Start = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddDays(7);

    [Fact]
    public void Scheduling_rejects_invalid_time_window_and_points_ceiling()
    {
        var id = Guid.NewGuid();
        Assert.Equal("competition.invalid_window", Assert.Throws<DomainRuleException>(
            () => Competition.Domain.Competitions.Competition.Schedule(id, "Winter Cup", End, Start, 100)).Code);
        Assert.Equal("competition.invalid_maximum_points", Assert.Throws<DomainRuleException>(
            () => Competition.Domain.Competitions.Competition.Schedule(id, "Winter Cup", Start, End, 0)).Code);
    }

    [Fact]
    public void Registration_closes_at_start_and_result_window_is_half_open()
    {
        var competition = Competition.Domain.Competitions.Competition.Schedule(Guid.NewGuid(), "Cup", Start, End, 100);
        competition.RequireRegistrationOpen(Start.AddTicks(-1));
        Assert.Equal("competition.registration_closed", Assert.Throws<DomainRuleException>(
            () => competition.RequireRegistrationOpen(Start)).Code);
        competition.RequireResultWindow(Start);
        Assert.Equal("competition.not_active", Assert.Throws<DomainRuleException>(
            () => competition.RequireResultWindow(End)).Code);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Scores_outside_policy_are_rejected(int points)
    {
        var competition = Competition.Domain.Competitions.Competition.Schedule(Guid.NewGuid(), "Cup", Start, End, 100);
        Assert.Equal("result.points_out_of_range", Assert.Throws<DomainRuleException>(
            () => competition.RequireValidPoints(points)).Code);
    }

    [Fact]
    public void Ranking_is_deterministic_and_tied_positions_are_shared()
    {
        var competitionId = Guid.NewGuid();
        var alpha = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var beta = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var gamma = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var rows = new[]
        {
            VerifiedResult.Create(Guid.NewGuid(), competitionId, gamma, 20, Start, "e3"),
            VerifiedResult.Create(Guid.NewGuid(), competitionId, beta, 40, Start, "e2"),
            VerifiedResult.Create(Guid.NewGuid(), competitionId, alpha, 40, Start, "e1")
        };
        var ranking = RankingPolicy.Calculate(rows);
        Assert.Equal(new[] { alpha, beta, gamma }, ranking.Select(x => x.ParticipantId));
        Assert.Equal(new[] { 1, 1, 3 }, ranking.Select(x => x.Rank));
    }
}
