using System.Security.Cryptography;
using System.Text;
using Competition.Application.Ports;
using Competition.Domain;
using Competition.Domain.Results;

namespace Competition.Application.UseCases;

public sealed record SubmitResultInput(Guid CompetitionId, Guid AuthenticatedParticipantId,
    string IdempotencyKey, string OpaqueEvidence);
public sealed record SubmitResultOutput(string Code, Guid? ResultId = null, int? VerifiedPoints = null);

public interface ISubmitResultInputBoundary
{
    Task ExecuteAsync(SubmitResultInput input, ISubmitResultOutputBoundary output, CancellationToken cancellationToken);
}
public interface ISubmitResultOutputBoundary { void Present(SubmitResultOutput response); }

public sealed class SubmitResultInteractor(
    ICompetitionGateway competitions,
    IEnrollmentGateway enrollments,
    IResultEvidencePort evidence,
    IResultGateway results,
    IUtcClock clock,
    IResultUuidSource ids) : ISubmitResultInputBoundary
{
    public async Task ExecuteAsync(SubmitResultInput input, ISubmitResultOutputBoundary output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (input.AuthenticatedParticipantId == Guid.Empty)
        {
            output.Present(new("actor.unauthenticated"));
            return;
        }
        if (input.CompetitionId == Guid.Empty || string.IsNullOrWhiteSpace(input.IdempotencyKey)
            || input.IdempotencyKey.Length > 100 || input.IdempotencyKey.Length < 8
            || string.IsNullOrWhiteSpace(input.OpaqueEvidence) || input.OpaqueEvidence.Length > 4096)
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

        try { competition.RequireResultWindow(clock.UtcNow); }
        catch (DomainRuleException ex) { output.Present(new(ex.Code)); return; }

        if (!await enrollments.ExistsAsync(input.CompetitionId, input.AuthenticatedParticipantId, cancellationToken))
        {
            output.Present(new("participant.not_enrolled"));
            return;
        }

        // The port implementation must establish trust independently of the caller's claims.
        var verified = await evidence.VerifyAsync(input.CompetitionId, input.AuthenticatedParticipantId,
            input.OpaqueEvidence, cancellationToken);
        if (verified is null)
        {
            output.Present(new("result.untrusted_evidence"));
            return;
        }

        // Evidence verification may take time. Recheck the competition window as close
        // to persistence as possible; the database gateway must enforce stricter
        // transaction-bound time rules if a production-grade cutoff is required.
        var acceptedAt = clock.UtcNow;
        try
        {
            competition.RequireResultWindow(acceptedAt);
            competition.RequireValidPoints(verified.Points);
        }
        catch (DomainRuleException ex) { output.Present(new(ex.Code)); return; }

        var verifiedResult = VerifiedResult.Create(ids.Next(), input.CompetitionId,
            input.AuthenticatedParticipantId, verified.Points, acceptedAt, verified.EvidenceId);
        var fingerprintBytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{input.CompetitionId:N}:{input.AuthenticatedParticipantId:N}:{input.OpaqueEvidence}"));
        var fingerprint = Convert.ToHexString(fingerprintBytes);
        var written = await results.AppendAsync(verifiedResult, input.IdempotencyKey, fingerprint, cancellationToken);
        output.Present(written.Status switch
        {
            ResultWriteStatus.Created => new SubmitResultOutput("result.accepted", written.Result?.Id, written.Result?.Points),
            ResultWriteStatus.Replayed => new SubmitResultOutput("result.replayed", written.Result?.Id, written.Result?.Points),
            ResultWriteStatus.CompetitionNotFound => new SubmitResultOutput("competition.not_found"),
            ResultWriteStatus.CompetitionClosed => new SubmitResultOutput("competition.not_active"),
            ResultWriteStatus.NotEnrolled => new SubmitResultOutput("participant.not_enrolled"),
            ResultWriteStatus.InvalidPoints => new SubmitResultOutput("result.points_out_of_range"),
            _ => new SubmitResultOutput("result.idempotency_conflict")
        });
    }
}
