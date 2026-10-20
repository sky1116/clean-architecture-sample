using Competition.Application.UseCases;

namespace Competition.Adapters.Rest;

/// <summary>The View Model is an adapter-owned transport-neutral rendering of output data.</summary>
public sealed record ApiViewModel(int StatusCode, string Code, object? Data = null);

/// <summary>One short-lived presenter per request; the interactor never creates a web response.</summary>
public sealed class RestPresenter :
    ICreateCompetitionOutputBoundary, IRegisterParticipantOutputBoundary,
    ISubmitResultOutputBoundary, IGetLeaderboardOutputBoundary, IGetPersonalHistoryOutputBoundary
{
    public ApiViewModel? ViewModel { get; private set; }

    public void Present(CreateCompetitionOutput value) =>
        ViewModel = new ApiViewModel(Status(value.Code), value.Code,
            value.CompetitionId is null ? null : new { value.CompetitionId });

    public void Present(RegisterParticipantOutput value) =>
        ViewModel = new ApiViewModel(Status(value.Code), value.Code);

    public void Present(SubmitResultOutput value) =>
        ViewModel = new ApiViewModel(Status(value.Code), value.Code,
            value.ResultId is null ? null : new { value.ResultId, value.VerifiedPoints });

    public void Present(GetLeaderboardOutput value) =>
        ViewModel = new ApiViewModel(Status(value.Code), value.Code,
            new { value.Rows, value.TotalCount });

    public void Present(GetPersonalHistoryOutput value) =>
        ViewModel = new ApiViewModel(Status(value.Code), value.Code,
            new { value.Entries, value.TotalCount });

    private static int Status(string code) => code switch
    {
        "competition.created" or "registration.accepted" or "result.accepted" => 201,
        "result.replayed" or "leaderboard.ready" or "history.ready" => 200,
        "actor.unauthenticated" => 401,
        "actor.forbidden" => 403,
        "competition.not_found" => 404,
        "competition.already_exists" or "registration.already_registered"
            or "result.idempotency_conflict" or "competition.not_active"
            or "competition.registration_closed" => 409,
        "result.untrusted_evidence" or "result.points_out_of_range"
            or "request.invalid" => 422,
        _ => 422
    };
}
