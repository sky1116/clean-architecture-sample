using Competition.Application.Ports;
using Competition.Application.UseCases;
using Xunit;

namespace Competition.Application.Tests;

public sealed class OutboxRecoveryInteractorTests
{
    private sealed class Clock : IUtcClock
    {
        public DateTimeOffset UtcNow => new(2027, 2, 1, 0, 0, 0, TimeSpan.Zero);
    }
    private sealed class Port : IOutboxRecoveryPort
    {
        public int Calls { get; private set; }
        public OutboxRecoveryStatus Status { get; set; } = OutboxRecoveryStatus.Requeued;
        public Task<OutboxRecoveryStatus> RedriveAsync(Guid id, Guid actor, string reason,
            DateTimeOffset now, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.NotEqual(Guid.Empty, actor);
            Assert.True(reason.Length >= 8);
            return Task.FromResult(Status);
        }
    }
    private sealed class Output : IRedriveOutboxOutputBoundary
    {
        public string? Code { get; private set; }
        public void Present(RedriveOutboxOutput response) => Code = response.Code;
    }

    [Fact]
    public async Task Unauthorized_user_cannot_requeue_any_event()
    {
        var port = new Port();
        var output = new Output();
        await new RedriveOutboxInteractor(port, new Clock()).ExecuteAsync(
            new RedriveOutboxInput(Guid.NewGuid(), Guid.NewGuid(), false,
                "Investigated delivery failure"), output, CancellationToken.None);
        Assert.Equal("actor.forbidden", output.Code);
        Assert.Equal(0, port.Calls);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("")]
    public async Task Missing_reason_never_reaches_persistence(string reason)
    {
        var port = new Port();
        var output = new Output();
        await new RedriveOutboxInteractor(port, new Clock()).ExecuteAsync(
            new RedriveOutboxInput(Guid.NewGuid(), Guid.NewGuid(), true, reason),
            output, CancellationToken.None);
        Assert.Equal("request.invalid", output.Code);
        Assert.Equal(0, port.Calls);
    }

    [Fact]
    public async Task Ineligible_event_does_not_report_a_success()
    {
        var port = new Port { Status = OutboxRecoveryStatus.NotEligible };
        var output = new Output();
        await new RedriveOutboxInteractor(port, new Clock()).ExecuteAsync(
            new RedriveOutboxInput(Guid.NewGuid(), Guid.NewGuid(), true,
                "Operator verified destination is repaired"),
            output, CancellationToken.None);
        Assert.Equal("outbox.not_eligible", output.Code);
        Assert.Equal(1, port.Calls);
    }
}
