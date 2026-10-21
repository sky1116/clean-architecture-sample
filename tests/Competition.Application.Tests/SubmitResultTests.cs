using Competition.Application.Ports;
using Competition.Application.UseCases;
using Competition.Domain.Competitions;
using Competition.Domain.Results;
using Xunit;

namespace Competition.Application.Tests;

public sealed class SubmitResultTests
{
    private static readonly DateTimeOffset Start = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private sealed class Clock : IUtcClock { public DateTimeOffset UtcNow => Start.AddMinutes(2); }
    private sealed class Ids : IResultUuidSource { public Guid Next() => Guid.Parse("00000000-0000-0000-0000-000000000099"); }
    private sealed class Presenter : ISubmitResultOutputBoundary
    {
        public SubmitResultOutput? Response { get; private set; }
        public void Present(SubmitResultOutput response) => Response = response;
    }
    private sealed class Competitions : ICompetitionGateway
    {
        public Task<Competition.Domain.Competitions.Competition?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<Competition.Domain.Competitions.Competition?>(
                Competition.Domain.Competitions.Competition.Schedule(id, "Contest", Start, Start.AddDays(1), 100));
        public Task<bool> CreateAsync(Competition.Domain.Competitions.Competition competition, CancellationToken cancellationToken)
            => Task.FromResult(true);
    }
    private sealed class Enrollments : IEnrollmentGateway
    {
        public bool IsEnrolled { get; init; } = true;
        public Task<bool> ExistsAsync(Guid competitionId, Guid participantId, CancellationToken cancellationToken)
            => Task.FromResult(IsEnrolled);
        public Task<EnrollmentWriteStatus> RegisterAsync(Guid competitionId, Guid participantId, CancellationToken cancellationToken)
            => Task.FromResult(EnrollmentWriteStatus.Created);
    }
    private sealed class Evidence : IResultEvidencePort
    {
        public bool IsValid { get; init; } = true;
        public int Points { get; init; } = 80;
        public Task<VerifiedEvidence?> VerifyAsync(Guid competitionId, Guid participantId, string opaqueEvidence,
            CancellationToken cancellationToken) => Task.FromResult<VerifiedEvidence?>(
                IsValid ? new(Points, "trusted-001") : null);
    }
    private sealed class Results : IResultGateway
    {
        public int Writes { get; private set; }
        public ResultWriteStatus Status { get; init; } = ResultWriteStatus.Created;
        public ResultWriteOutcome? Existing { get; set; }
        public int LookupCalls { get; private set; }
        public Task<ResultWriteOutcome?> FindExistingAsync(Guid competitionId,
            Guid participantId, string key, string fingerprint,
            CancellationToken cancellationToken)
        {
            LookupCalls++;
            return Task.FromResult(Existing);
        }
        public Task<ResultWriteOutcome> AppendAsync(VerifiedResult proposed, string key, string fingerprint,
            CancellationToken cancellationToken)
        {
            Writes++;
            return Task.FromResult(new ResultWriteOutcome(Status,
                Status == ResultWriteStatus.Conflict ? null : proposed));
        }
        public Task<IReadOnlyList<VerifiedResult>> ReadCompetitionAsync(Guid competitionId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<VerifiedResult>>(Array.Empty<VerifiedResult>());
        public Task<IReadOnlyList<VerifiedResult>> ReadParticipantAsync(Guid competitionId, Guid participantId,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<VerifiedResult>>(Array.Empty<VerifiedResult>());
    }

    private static async Task<(string? Code, int Writes)> ExecuteAsync(Enrollments? enrollment = null,
        Evidence? evidence = null, Results? results = null, Guid? actor = null)
    {
        var gateway = results ?? new Results();
        var presenter = new Presenter();
        var useCase = new SubmitResultInteractor(new Competitions(), enrollment ?? new Enrollments(),
            evidence ?? new Evidence(), gateway, new Clock(), new Ids());
        await useCase.ExecuteAsync(new SubmitResultInput(Guid.NewGuid(), actor ?? Guid.NewGuid(),
            "request-1234", "trusted-proof"), presenter, CancellationToken.None);
        return (presenter.Response?.Code, gateway.Writes);
    }

    [Fact]
    public async Task Trusted_enrolled_result_is_written_once()
    {
        var actual = await ExecuteAsync();
        Assert.Equal("result.accepted", actual.Code);
        Assert.Equal(1, actual.Writes);
    }

    [Fact]
    public async Task Untrusted_evidence_never_enters_the_ledger()
    {
        var actual = await ExecuteAsync(evidence: new Evidence { IsValid = false });
        Assert.Equal("result.untrusted_evidence", actual.Code);
        Assert.Equal(0, actual.Writes);
    }

    [Fact]
    public async Task Unenrolled_participants_cannot_submit()
    {
        var actual = await ExecuteAsync(enrollment: new Enrollments { IsEnrolled = false });
        Assert.Equal("participant.not_enrolled", actual.Code);
        Assert.Equal(0, actual.Writes);
    }

    [Fact]
    public async Task Server_checked_score_outside_policy_is_rejected()
    {
        var actual = await ExecuteAsync(evidence: new Evidence { Points = 101 });
        Assert.Equal("result.points_out_of_range", actual.Code);
        Assert.Equal(0, actual.Writes);
    }

    [Fact]
    public async Task Same_key_with_different_payload_produces_conflict()
    {
        var actual = await ExecuteAsync(results: new Results { Status = ResultWriteStatus.Conflict });
        Assert.Equal("result.idempotency_conflict", actual.Code);
    }

    [Fact]
    public async Task Missing_authenticated_actor_never_writes()
    {
        var actual = await ExecuteAsync(actor: Guid.Empty);
        Assert.Equal("actor.unauthenticated", actual.Code);
        Assert.Equal(0, actual.Writes);
    }
    private sealed class AdvancingClock : IUtcClock
    {
        private int _calls;

        public DateTimeOffset UtcNow => Interlocked.Increment(ref _calls) == 1
            ? Start.AddMinutes(2)
            : Start.AddDays(1);
    }

    [Fact]
    public async Task Verification_completed_after_competition_end_must_not_write_result()
    {
        var gateway = new Results();
        var presenter = new Presenter();
        var useCase = new SubmitResultInteractor(new Competitions(), new Enrollments(),
            new Evidence(), gateway, new AdvancingClock(), new Ids());

        await useCase.ExecuteAsync(new SubmitResultInput(Guid.NewGuid(), Guid.NewGuid(),
            "window-race-request", "trusted-proof"), presenter, CancellationToken.None);

        Assert.Equal("competition.not_active", presenter.Response?.Code);
        Assert.Equal(0, gateway.Writes);
    }

    [Fact]
    public async Task A_previously_accepted_exact_replay_short_circuits_current_clock_and_proof_validation()
    {
        var gateway = new Results
        {
            Existing = new ResultWriteOutcome(ResultWriteStatus.Replayed,
                VerifiedResult.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                    45, Start, "original"))
        };
        var presenter = new Presenter();
        var useCase = new SubmitResultInteractor(new Competitions(), new Enrollments(),
            new Evidence { IsValid = false }, gateway, new AdvancingClock(), new Ids());
        await useCase.ExecuteAsync(new SubmitResultInput(Guid.NewGuid(), Guid.NewGuid(),
            "request-1234", "same-previous-proof"), presenter, CancellationToken.None);
        Assert.Equal("result.replayed", presenter.Response?.Code);
        Assert.Equal(45, presenter.Response?.VerifiedPoints);
        Assert.Equal(1, gateway.LookupCalls);
        Assert.Equal(0, gateway.Writes);
    }

    [Fact]
    public async Task Existing_key_with_a_different_fingerprint_is_rejected_without_reverification_or_write()
    {
        var gateway = new Results
        {
            Existing = new ResultWriteOutcome(ResultWriteStatus.Conflict, null)
        };
        var presenter = new Presenter();
        await new SubmitResultInteractor(new Competitions(), new Enrollments(),
            new Evidence { IsValid = false }, gateway, new Clock(), new Ids())
            .ExecuteAsync(new SubmitResultInput(Guid.NewGuid(), Guid.NewGuid(),
                "request-1234", "altered-proof"), presenter, CancellationToken.None);
        Assert.Equal("result.idempotency_conflict", presenter.Response?.Code);
        Assert.Equal(0, gateway.Writes);
    }

}
