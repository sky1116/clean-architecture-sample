using Competition.Application.Ports;

namespace Competition.Application.UseCases;

public sealed record RedriveOutboxInput(Guid EventId, Guid AuthenticatedAdministratorId,
    bool IsAuthorizedAdministrator, string Reason);
public sealed record RedriveOutboxOutput(string Code);

public interface IRedriveOutboxInputBoundary
{
    Task ExecuteAsync(RedriveOutboxInput input, IRedriveOutboxOutputBoundary output,
        CancellationToken cancellationToken);
}
public interface IRedriveOutboxOutputBoundary { void Present(RedriveOutboxOutput result); }

public sealed class RedriveOutboxInteractor(IOutboxRecoveryPort recovery, IUtcClock clock)
    : IRedriveOutboxInputBoundary
{
    public async Task ExecuteAsync(RedriveOutboxInput input, IRedriveOutboxOutputBoundary output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!input.IsAuthorizedAdministrator || input.AuthenticatedAdministratorId == Guid.Empty)
        {
            output.Present(new("actor.forbidden"));
            return;
        }
        if (input.EventId == Guid.Empty || string.IsNullOrWhiteSpace(input.Reason)
            || input.Reason.Trim().Length is < 8 or > 300)
        {
            output.Present(new("request.invalid"));
            return;
        }

        var result = await recovery.RedriveAsync(input.EventId,
            input.AuthenticatedAdministratorId, input.Reason.Trim(),
            clock.UtcNow, cancellationToken);
        output.Present(new(result == OutboxRecoveryStatus.Requeued
            ? "outbox.requeued" : "outbox.not_eligible"));
    }
}
