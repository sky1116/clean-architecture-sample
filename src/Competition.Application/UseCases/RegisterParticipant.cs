using Competition.Application.Ports;
using Competition.Domain;

namespace Competition.Application.UseCases;

public sealed record RegisterParticipantInput(Guid CompetitionId, Guid AuthenticatedParticipantId);
public sealed record RegisterParticipantOutput(string Code);
public interface IRegisterParticipantInputBoundary
{
    Task ExecuteAsync(RegisterParticipantInput input, IRegisterParticipantOutputBoundary output, CancellationToken cancellationToken);
}
public interface IRegisterParticipantOutputBoundary { void Present(RegisterParticipantOutput response); }

public sealed class RegisterParticipantInteractor(ICompetitionGateway competitions,
    IEnrollmentGateway enrollments, IUtcClock clock) : IRegisterParticipantInputBoundary
{
    public async Task ExecuteAsync(RegisterParticipantInput input, IRegisterParticipantOutputBoundary output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (input.AuthenticatedParticipantId == Guid.Empty)
        {
            output.Present(new("actor.unauthenticated"));
            return;
        }
        if (input.CompetitionId == Guid.Empty)
        {
            output.Present(new("request.invalid"));
            return;
        }
        var competition = await competitions.FindAsync(input.CompetitionId, cancellationToken);
        if (competition is null)
        {
            output.Present(new("competition.not_found"));
            return;
        }
        try { competition.RequireRegistrationOpen(clock.UtcNow); }
        catch (DomainRuleException ex) { output.Present(new(ex.Code)); return; }
        var status = await enrollments.RegisterAsync(input.CompetitionId, input.AuthenticatedParticipantId,
            cancellationToken);
        output.Present(new(status switch
        {
            EnrollmentWriteStatus.Created => "registration.accepted",
            EnrollmentWriteStatus.AlreadyExists => "registration.already_registered",
            EnrollmentWriteStatus.Closed => "competition.registration_closed",
            EnrollmentWriteStatus.CompetitionNotFound => "competition.not_found",
            _ => throw new InvalidOperationException("Unknown enrollment outcome")
        }));
    }
}
