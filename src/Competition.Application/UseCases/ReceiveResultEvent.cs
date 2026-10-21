using Competition.Application.Ports;

namespace Competition.Application.UseCases;

/// <summary>
/// Only a trusted adapter may supply this verified, framework-independent message.
/// HTTP signature verification and JSON parsing live outside the use case.
/// </summary>
public sealed record ReceiveResultEventInput(
    Guid EventId, Guid CompetitionId, Guid ResultId, Guid? ParticipantId,
    string EventType, int Points, int Revision, string PayloadDigest);

public sealed record ReceiveResultEventOutput(string Code);

public interface IReceiveResultEventInputBoundary
{
    Task ExecuteAsync(ReceiveResultEventInput input,
        IReceiveResultEventOutputBoundary output, CancellationToken cancellationToken);
}

public interface IReceiveResultEventOutputBoundary
{
    void Present(ReceiveResultEventOutput result);
}

public sealed class ReceiveResultEventInteractor(IResultInboxPort inbox)
    : IReceiveResultEventInputBoundary
{
    public async Task ExecuteAsync(ReceiveResultEventInput input,
        IReceiveResultEventOutputBoundary output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        // Messages are already authenticated by the outer inbound adapter.
        // Here we enforce versioned application input rules, not HTTP details.
        var accepted = input.EventType == "competition.result.accepted.v1";
        var corrected = input.EventType == "competition.result.corrected.v1";
        if (input.EventId == Guid.Empty || input.CompetitionId == Guid.Empty ||
            input.ResultId == Guid.Empty || (!accepted && !corrected) ||
            input.Points is < 0 or > 1_000_000 ||
            input.PayloadDigest.Length != 64 ||
            !input.PayloadDigest.All(Uri.IsHexDigit) ||
            (accepted && (input.ParticipantId is null || input.ParticipantId == Guid.Empty ||
                          input.Revision != 0)) ||
            (corrected && (input.ParticipantId is not null || input.Revision < 1)))
        {
            output.Present(new("inbox.invalid_event"));
            return;
        }

        var status = await inbox.ApplyAsync(input, cancellationToken);
        output.Present(new(status switch
        {
            InboxWriteStatus.Applied => "inbox.applied",
            InboxWriteStatus.Duplicate => "inbox.duplicate",
            InboxWriteStatus.DependencyMissing => "inbox.dependency_missing",
            InboxWriteStatus.Conflict => "inbox.conflict",
            _ => throw new InvalidOperationException("Unknown inbox result")
        }));
    }
}
