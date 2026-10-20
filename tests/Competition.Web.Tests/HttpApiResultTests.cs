using System.Diagnostics;
using System.Text.Json;
using Competition.Adapters.Rest;
using Competition.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Competition.Web.Tests;

public sealed class HttpApiResultTests
{
    [Fact]
    public async Task Application_outcome_is_logged_and_returned_without_untrusted_proof()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        await using var response = new MemoryStream();
        context.Response.Body = response;
        using var activity = new Activity("presenter-response").Start();

        await new HttpApiResult(new ApiViewModel(422, "result.untrusted_evidence")).ExecuteAsync(context);

        Assert.Equal(422, context.Response.StatusCode);
        Assert.Equal("result.untrusted_evidence", activity.GetTagItem("application.outcome_code"));
        response.Position = 0;
        using var json = await JsonDocument.ParseAsync(response);
        Assert.Equal("result.untrusted_evidence", json.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("OpaqueEvidence", json.RootElement.ToString(), StringComparison.Ordinal);
    }
}
