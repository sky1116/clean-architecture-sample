using Competition.Application.Ports;
using Competition.Application.UseCases;
using Xunit;

namespace Competition.Application.Tests;

public sealed class RegisterParticipantReplayTests
{
    private static readonly DateTimeOffset Now = new(2027, 1, 2, 0, 0, 0, TimeSpan.Zero);
    private sealed class ExpiredClock : IUtcClock { public DateTimeOffset UtcNow => Now; }
    private sealed class CompetitionPort : ICompetitionGateway
    {
        public Task<Competition.Domain.Competitions.Competition?> FindAsync(Guid id,
            CancellationToken cancellationToken) =>
            Task.FromResult<Competition.Domain.Competitions.Competition?>(
                Competition.Domain.Competitions.Competition.Schedule(
                    id, "Past registration cutoff", Now.AddDays(-1),
                    Now.AddDays(1), 100));
        public Task<bool> CreateAsync(Competition.Domain.Competitions.Competition c,
            CancellationToken ct) => Task.FromResult(true);
    }
    private sealed class EnrollmentPort(bool exists) : IEnrollmentGateway
    {
        public int Attempts { get; private set; }
        public Task<bool> ExistsAsync(Guid competition, Guid participant, CancellationToken ct) =>
            Task.FromResult(exists);
        public Task<EnrollmentWriteStatus> RegisterAsync(Guid competition, Guid participant,
            CancellationToken ct)
        {
            Attempts++;
            return Task.FromResult(EnrollmentWriteStatus.Created);
        }
    }
    private sealed class Output : IRegisterParticipantOutputBoundary
    {
        public string? Code { get; private set; }
        public void Present(RegisterParticipantOutput output) => Code = output.Code;
    }

    [Fact]
    public async Task Accepted_existing_registration_is_visible_after_the_cutoff_without_a_new_write()
    {
        var port = new EnrollmentPort(true);
        var output = new Output();
        await new RegisterParticipantInteractor(new CompetitionPort(), port, new ExpiredClock())
            .ExecuteAsync(new RegisterParticipantInput(Guid.NewGuid(), Guid.NewGuid()),
                output, CancellationToken.None);
        Assert.Equal("registration.already_registered", output.Code);
        Assert.Equal(0, port.Attempts);
    }

    [Fact]
    public async Task New_registration_after_cutoff_must_not_reach_write_gateway()
    {
        var port = new EnrollmentPort(false);
        var output = new Output();
        await new RegisterParticipantInteractor(new CompetitionPort(), port, new ExpiredClock())
            .ExecuteAsync(new RegisterParticipantInput(Guid.NewGuid(), Guid.NewGuid()),
                output, CancellationToken.None);
        Assert.Equal("competition.registration_closed", output.Code);
        Assert.Equal(0, port.Attempts);
    }
}
