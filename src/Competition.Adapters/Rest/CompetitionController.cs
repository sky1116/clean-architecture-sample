using Competition.Application.UseCases;

namespace Competition.Adapters.Rest;

/// <summary>Framework-neutral controller. An outer HTTP driver only maps routes and claims.</summary>
public sealed class CompetitionController(
    ICreateCompetitionInputBoundary create,
    IRegisterParticipantInputBoundary registration,
    ISubmitResultInputBoundary submission,
    IGetLeaderboardInputBoundary leaderboard,
    IGetPersonalHistoryInputBoundary history,
    ICorrectResultInputBoundary correction,
    IReceiveResultEventInputBoundary inbox,
    IRedriveOutboxInputBoundary outboxRecovery)
{
    public async Task<ApiViewModel> CreateAsync(CreateCompetitionInput input, CancellationToken cancellationToken)
    {
        var presenter = new RestPresenter();
        await create.ExecuteAsync(input, presenter, cancellationToken);
        return presenter.ViewModel!;
    }

    public async Task<ApiViewModel> RegisterAsync(RegisterParticipantInput input, CancellationToken cancellationToken)
    {
        var presenter = new RestPresenter();
        await registration.ExecuteAsync(input, presenter, cancellationToken);
        return presenter.ViewModel!;
    }

    public async Task<ApiViewModel> SubmitAsync(SubmitResultInput input, CancellationToken cancellationToken)
    {
        var presenter = new RestPresenter();
        await submission.ExecuteAsync(input, presenter, cancellationToken);
        return presenter.ViewModel!;
    }

    public async Task<ApiViewModel> LeaderboardAsync(GetLeaderboardInput input, CancellationToken cancellationToken)
    {
        var presenter = new RestPresenter();
        await leaderboard.ExecuteAsync(input, presenter, cancellationToken);
        return presenter.ViewModel!;
    }

    public async Task<ApiViewModel> RedriveAsync(
        RedriveOutboxInput input, CancellationToken cancellationToken)
    {
        var presenter = new RestPresenter();
        await outboxRecovery.ExecuteAsync(input, presenter, cancellationToken);
        return presenter.ViewModel!;
    }

    public async Task<ApiViewModel> ReceiveAsync(
        ReceiveResultEventInput input, CancellationToken cancellationToken)
    {
        var presenter = new RestPresenter();
        await inbox.ExecuteAsync(input, presenter, cancellationToken);
        return presenter.ViewModel!;
    }

    public async Task<ApiViewModel> CorrectAsync(CorrectResultInput input, CancellationToken cancellationToken)
    {
        var presenter = new RestPresenter();
        await correction.ExecuteAsync(input, presenter, cancellationToken);
        return presenter.ViewModel!;
    }

    public async Task<ApiViewModel> HistoryAsync(GetPersonalHistoryInput input, CancellationToken cancellationToken)
    {
        var presenter = new RestPresenter();
        await history.ExecuteAsync(input, presenter, cancellationToken);
        return presenter.ViewModel!;
    }
}
