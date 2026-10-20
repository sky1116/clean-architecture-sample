using Competition.Application.Ports;
using Competition.Domain.Rankings;

namespace Competition.Application.UseCases;

public sealed record GetLeaderboardInput(Guid CompetitionId, int Offset = 0, int Limit = 20);
public sealed record GetLeaderboardOutput(string Code, IReadOnlyList<Standing> Rows, int TotalCount);
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
            output.Present(new("request.invalid", Array.Empty<Standing>(), 0));
            return;
        }
        if (await competitions.FindAsync(input.CompetitionId, cancellationToken) is null)
        {
            output.Present(new("competition.not_found", Array.Empty<Standing>(), 0));
            return;
        }
        // Correctness-first reference: compute from authoritative accepted results, not a stale cache.
        var ranking = RankingPolicy.Calculate(await results.ReadCompetitionAsync(input.CompetitionId, cancellationToken));
        output.Present(new("leaderboard.ready", ranking.Skip(input.Offset).Take(input.Limit).ToArray(), ranking.Count));
    }
}
