using Competition.Application.Ports;
using Competition.Domain;

namespace Competition.Application.UseCases;

public sealed record CreateCompetitionInput(Guid Id, string Name, DateTimeOffset StartsAt,
    DateTimeOffset EndsAt, int MaximumPoints, bool AuthenticatedAdministrator);
public sealed record CreateCompetitionOutput(string Code, Guid? CompetitionId = null);
public interface ICreateCompetitionInputBoundary
{
    Task ExecuteAsync(CreateCompetitionInput input, ICreateCompetitionOutputBoundary output, CancellationToken cancellationToken);
}
public interface ICreateCompetitionOutputBoundary { void Present(CreateCompetitionOutput response); }

public sealed class CreateCompetitionInteractor(ICompetitionGateway gateway) : ICreateCompetitionInputBoundary
{
    public async Task ExecuteAsync(CreateCompetitionInput input, ICreateCompetitionOutputBoundary output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!input.AuthenticatedAdministrator)
        {
            output.Present(new("actor.forbidden"));
            return;
        }
        try
        {
            var competition = Competition.Domain.Competitions.Competition.Schedule(
                input.Id, input.Name, input.StartsAt, input.EndsAt, input.MaximumPoints);
            var created = await gateway.CreateAsync(competition, cancellationToken);
            output.Present(created
                ? new("competition.created", competition.Id)
                : new("competition.already_exists"));
        }
        catch (DomainRuleException ex) { output.Present(new(ex.Code)); }
    }
}
