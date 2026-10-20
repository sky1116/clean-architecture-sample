using System.Diagnostics;
using Competition.Adapters.Rest;

namespace Competition.Web;

/// <summary>
/// The HTTP driver renders a Presenter-created View Model. Observability never enters
/// the interactor, and only stable outcome codes are logged (never proof material).
/// </summary>
public sealed class HttpApiResult(ApiViewModel viewModel) : IResult
{
    public async Task ExecuteAsync(HttpContext context)
    {
        Activity.Current?.SetTag("application.outcome_code", viewModel.Code);
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Competition.Outcome");
        logger.LogInformation(
            "UseCaseOutcome Code={OutcomeCode} HttpStatus={HttpStatus} TraceId={TraceId}",
            viewModel.Code, viewModel.StatusCode, Activity.Current?.TraceId.ToString()
                ?? context.TraceIdentifier);
        await Results.Json(new { code = viewModel.Code, data = viewModel.Data },
            statusCode: viewModel.StatusCode).ExecuteAsync(context);
    }
}
