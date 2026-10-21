using Competition.Application.Ports;
using Competition.Domain;

namespace Competition.Application.UseCases;

public sealed record CorrectResultInput(Guid CompetitionId, Guid ResultId, Guid AdministratorId,
    bool IsAuthorizedAdministrator, int ExpectedRevision, int ReplacementPoints, string Reason);
public sealed record CorrectResultOutput(string Code, int? Revision = null);

public interface ICorrectResultInputBoundary
{
    Task ExecuteAsync(CorrectResultInput input, ICorrectResultOutputBoundary output, CancellationToken cancellationToken);
}
public interface ICorrectResultOutputBoundary { void Present(CorrectResultOutput response); }

/// <summary>Authorization is enforced here in addition to the outer HTTP policy.</summary>
public sealed class CorrectResultInteractor(ICompetitionGateway competitions,
    IResultCorrectionPort corrections, IUtcClock clock) : ICorrectResultInputBoundary
{
    public async Task ExecuteAsync(CorrectResultInput input, ICorrectResultOutputBoundary output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!input.IsAuthorizedAdministrator || input.AdministratorId == Guid.Empty)
        {
            output.Present(new("actor.forbidden"));
            return;
        }
        if (input.CompetitionId == Guid.Empty || input.ResultId == Guid.Empty
            || input.ExpectedRevision < 0 || string.IsNullOrWhiteSpace(input.Reason)
            || input.Reason.Trim().Length is < 8 or > 500)
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
        try { competition.RequireValidPoints(input.ReplacementPoints); }
        catch (DomainRuleException ex) { output.Present(new(ex.Code)); return; }
        var written = await corrections.AppendAsync(input.CompetitionId, input.ResultId,
            input.AdministratorId, input.ExpectedRevision, input.ReplacementPoints,
            input.Reason.Trim(), clock.UtcNow, cancellationToken);
        output.Present(written.Status switch
        {
            CorrectionStatus.Applied => new("result.correction_applied", written.Revision),
            CorrectionStatus.NotFound => new("result.not_found"),
            _ => new("result.version_conflict")
        });
    }
}
