using Competition.Adapters.Rest;
using Competition.Application.UseCases;
using Xunit;

namespace Competition.Adapters.Tests;

public sealed class PresenterTests
{
    [Fact]
    public void Accepted_result_maps_to_created_status_without_exposing_evidence()
    {
        var presenter = new RestPresenter();
        presenter.Present(new SubmitResultOutput("result.accepted", Guid.NewGuid(), 45));
        Assert.Equal(201, presenter.ViewModel?.StatusCode);
        var json = System.Text.Json.JsonSerializer.Serialize(presenter.ViewModel);
        Assert.Contains("VerifiedPoints", json);
        Assert.DoesNotContain("OpaqueEvidence", json);
    }

    [Theory]
    [InlineData("result.untrusted_evidence", 422)]
    [InlineData("actor.unauthenticated", 401)]
    [InlineData("actor.forbidden", 403)]
    [InlineData("result.idempotency_conflict", 409)]
    [InlineData("result.replayed", 200)]
    public void Presenter_exposes_stable_error_status_mapping(string code, int expectedStatus)
    {
        var presenter = new RestPresenter();
        presenter.Present(new SubmitResultOutput(code));
        Assert.Equal(expectedStatus, presenter.ViewModel?.StatusCode);
    }
}
