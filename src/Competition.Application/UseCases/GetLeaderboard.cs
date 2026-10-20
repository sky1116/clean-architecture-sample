using Competition.Application.Ports;
using Competition.Domain.Rankings;

namespace Competition.Application.UseCases;

public sealed record GetLeaderboardInput(Guid CompetitionId, int Offset = 0, int Limit = 20);
// A plain output DTO: the Presenter never receives a domain Standing entity.
public sealed record LeaderboardRow(Guid ParticipantId, long TotalPoints, int Rank, int ResultCount);
public sealed record GetLeaderboardOutput(string Code, IReadOnlyList<LeaderboardRow> Rows, int TotalCount);
public interface IGetLeaderboardInputBoundary
{
    Task ExecuteAsync(GetLeaderboardInput input, IGetLeaderboardOutputBoundary output, CancellationToken cancellationToken);
}
public interface IGetLeaderboardOutputBoundary { void Present(GetLeaderboardOutput response); }

public sealed class GetLeaderboardInteractor(ICompetitionGateway competitions, IResultGateway results)
    : IGetLeaderboardInputBoundary
{
    public async Task ExecuteAsync(GetLeaderboardInput input, IGetLeaderboardOutputBoundary output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (input.CompetitionId == Guid.Empty || input.Offset < 0 || input.Limit is < 1 or > 100)
        {
            output.Present(new("request.invalid", Array.Empty<LeaderboardRow>(), 0));
            return;
        }
        if (await competitions.FindAsync(input.CompetitionId, cancellationToken) is null)
        {
            output.Present(new("competition.not_found", Array.Empty<LeaderboardRow>(), 0));
            return;
        }
        var ranking = RankingPolicy.Calculate(await results.ReadCompetitionAsync(input.CompetitionId, cancellationToken));
        var rows = ranking.Skip(input.Offset).Take(input.Limit).Select(s =>
            new LeaderboardRow(s.ParticipantId, s.TotalPoints, s.Rank, s.ResultCount)).ToArray();
        output.Present(new("leaderboard.ready", rows, ranking.Count));
    }
}
