using Competition.Application.Ports;
using Competition.Domain.Results;

namespace Competition.Application.UseCases;

public sealed record GetPersonalHistoryInput(Guid CompetitionId, Guid AuthenticatedParticipantId,
    int Offset = 0, int Limit = 20);
public sealed record GetPersonalHistoryOutput(string Code, IReadOnlyList<VerifiedResult> Entries, int TotalCount);

public interface IGetPersonalHistoryInputBoundary
{
    Task ExecuteAsync(GetPersonalHistoryInput input, IGetPersonalHistoryOutputBoundary output,
        CancellationToken cancellationToken);
}
public interface IGetPersonalHistoryOutputBoundary { void Present(GetPersonalHistoryOutput response); }

public sealed class GetPersonalHistoryInteractor(ICompetitionGateway competitions,
    IResultGateway results) : IGetPersonalHistoryInputBoundary
{
    public async Task ExecuteAsync(GetPersonalHistoryInput input, IGetPersonalHistoryOutputBoundary output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (input.AuthenticatedParticipantId == Guid.Empty)
        {
            output.Present(new("actor.unauthenticated", Array.Empty<VerifiedResult>(), 0));
            return;
        }
        if (input.CompetitionId == Guid.Empty || input.Offset < 0 || input.Limit is < 1 or > 100)
        {
            output.Present(new("request.invalid", Array.Empty<VerifiedResult>(), 0));
            return;
        }
        if (await competitions.FindAsync(input.CompetitionId, cancellationToken) is null)
        {
            output.Present(new("competition.not_found", Array.Empty<VerifiedResult>(), 0));
            return;
        }
        var journal = await results.ReadParticipantAsync(input.CompetitionId,
            input.AuthenticatedParticipantId, cancellationToken);
        var ordered = journal.OrderByDescending(x => x.RecordedAt).ThenBy(x => x.Id).ToArray();
        output.Present(new("history.ready", ordered.Skip(input.Offset).Take(input.Limit).ToArray(), ordered.Length));
    }
}
