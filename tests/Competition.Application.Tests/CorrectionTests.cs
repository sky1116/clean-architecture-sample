using Competition.Application.Ports;
using Competition.Application.UseCases;
using Competition.Domain.Competitions;
using Xunit;

namespace Competition.Application.Tests;

public sealed class CorrectionTests
{
    private sealed class Clock : IUtcClock
    {
        public DateTimeOffset UtcNow => new(2027, 1, 10, 0, 0, 0, TimeSpan.Zero);
    }
    private sealed class Competitions : ICompetitionGateway
    {
        public Task<Competition.Domain.Competitions.Competition?> FindAsync(Guid id, CancellationToken ct) =>
            Task.FromResult<Competition.Domain.Competitions.Competition?>(
                Competition.Domain.Competitions.Competition.Schedule(id, "Cup",
                    new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2027, 1, 7, 0, 0, 0, TimeSpan.Zero), 100));
        public Task<bool> CreateAsync(Competition.Domain.Competitions.Competition competition, CancellationToken ct)
            => Task.FromResult(true);
    }
    private sealed class CorrectionGateway : IResultCorrectionPort
    {
        public int Writes { get; private set; }
        public CorrectionOutcome ToReturn { get; init; } = new(CorrectionStatus.Applied, 1);
        public Task<CorrectionOutcome> AppendAsync(Guid contest, Guid result, Guid admin, int expected, int points,
            string reason, DateTimeOffset now, CancellationToken ct)
        {
            Writes++;
            return Task.FromResult(ToReturn);
        }
    }
    private sealed class Presenter : ICorrectResultOutputBoundary
    {
        public CorrectResultOutput? Response { get; private set; }
        public void Present(CorrectResultOutput response) => Response = response;
    }

    [Theory]
    [InlineData(false, 42, "A justified correction", "actor.forbidden")]
    [InlineData(true, 110, "A justified correction", "result.points_out_of_range")]
    [InlineData(true, 42, "short", "request.invalid")]
    [InlineData(true, 42, "A justified correction", "result.correction_applied")]
    public async Task Authorization_domain_rules_and_reason_govern_corrections(bool admin, int points,
        string reason, string expected)
    {
        var gateway = new CorrectionGateway();
        var presenter = new Presenter();
        await new CorrectResultInteractor(new Competitions(), gateway, new Clock())
            .ExecuteAsync(new CorrectResultInput(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                admin, 0, points, reason), presenter, CancellationToken.None);
        Assert.Equal(expected, presenter.Response?.Code);
        Assert.Equal(expected == "result.correction_applied" ? 1 : 0, gateway.Writes);
    }

    [Fact]
    public async Task Concurrency_conflict_is_not_reported_as_a_success()
    {
        var gateway = new CorrectionGateway {
            ToReturn = new(CorrectionStatus.VersionConflict, null)
        };
        var presenter = new Presenter();
        await new CorrectResultInteractor(new Competitions(), gateway, new Clock()).ExecuteAsync(
            new CorrectResultInput(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                true, 1, 50, "Correcting a disputed result"), presenter, CancellationToken.None);
        Assert.Equal("result.version_conflict", presenter.Response?.Code);
    }
}
