using Competition.Application.Ports;
using Competition.Application.UseCases;
using Xunit;

namespace Competition.Application.Tests;

public sealed class InboxInteractorTests
{
    private sealed class Storage : IResultInboxPort
    {
        public int Calls { get; private set; }
        public InboxWriteStatus ToReturn { get; set; } = InboxWriteStatus.Applied;
        public Task<InboxWriteStatus> ApplyAsync(ReceiveResultEventInput message,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(ToReturn);
        }
    }
    private sealed class Presenter : IReceiveResultEventOutputBoundary
    {
        public string? Code { get; private set; }
        public void Present(ReceiveResultEventOutput result) => Code = result.Code;
    }

    private static ReceiveResultEventInput Accepted() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        "competition.result.accepted.v1", 50, 0, new string('A', 64));

    [Fact]
    public async Task Valid_authenticated_input_reaches_port_once()
    {
        var port = new Storage();
        var output = new Presenter();
        await new ReceiveResultEventInteractor(port).ExecuteAsync(
            Accepted(), output, CancellationToken.None);
        Assert.Equal("inbox.applied", output.Code);
        Assert.Equal(1, port.Calls);
    }

    [Fact]
    public async Task Unsupported_type_and_invalid_identity_never_reach_persistence()
    {
        var port = new Storage();
        var output = new Presenter();
        await new ReceiveResultEventInteractor(port).ExecuteAsync(
            Accepted() with { EventType = "competition.result.unknown.v1" },
            output, CancellationToken.None);
        Assert.Equal("inbox.invalid_event", output.Code);
        await new ReceiveResultEventInteractor(port).ExecuteAsync(
            Accepted() with { CompetitionId = Guid.Empty }, output, CancellationToken.None);
        Assert.Equal("inbox.invalid_event", output.Code);
        Assert.Equal(0, port.Calls);
    }

    [Fact]
    public async Task Correction_requires_a_positive_revision_and_absent_player_identifier()
    {
        var port = new Storage();
        var output = new Presenter();
        var correction = Accepted() with {
            EventType = "competition.result.corrected.v1",
            Revision = 1,
            ParticipantId = null
        };
        await new ReceiveResultEventInteractor(port).ExecuteAsync(
            correction, output, CancellationToken.None);
        Assert.Equal("inbox.applied", output.Code);
        await new ReceiveResultEventInteractor(port).ExecuteAsync(
            correction with { Revision = 0 }, output, CancellationToken.None);
        Assert.Equal("inbox.invalid_event", output.Code);
        Assert.Equal(1, port.Calls);
    }

    [Theory]
    [InlineData(InboxWriteStatus.Duplicate, "inbox.duplicate")]
    [InlineData(InboxWriteStatus.Conflict, "inbox.conflict")]
    [InlineData(InboxWriteStatus.DependencyMissing, "inbox.dependency_missing")]
    public async Task Port_results_are_not_misreported_as_success(
        InboxWriteStatus status, string code)
    {
        var port = new Storage { ToReturn = status };
        var output = new Presenter();
        await new ReceiveResultEventInteractor(port).ExecuteAsync(
            Accepted(), output, CancellationToken.None);
        Assert.Equal(code, output.Code);
    }
}
