using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Competition.Web.Tests;

/// <summary>Actual ASP.NET Core routing and authorization; no fake controller method calls.</summary>
public sealed class HttpAuthorizationBoundaryTests
{
    private sealed class ApiFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Never opened for these unauthorized requests.
                    ["ConnectionStrings:Competitions"] =
                        "Host=127.0.0.1;Database=unused;Username=unused;Password=unused",
                    ["Authentication:Authority"] = "https://identity.example",
                    ["Authentication:Audience"] = "competitions-api",
                    // Deliberately inert test-only bytes, not any real signing authority key.
                    ["TrustedAuthority:SigningKeyBase64"] =
                        Convert.ToBase64String(new byte[32]),
                    ["Database:BootstrapOnStart"] = "false"
                });
            });
        }
    }

    [Fact]
    public async Task Liveness_endpoint_is_public_and_reports_only_process_status()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("alive", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Protected_routes_reject_anonymous_clients_before_use_cases_or_storage()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var competitionId = Guid.NewGuid();

        using var registration = await client.PostAsJsonAsync(
            $"/api/competitions/{competitionId}/participants", new { });
        using var submission = await client.PostAsJsonAsync(
            $"/api/competitions/{competitionId}/results",
            new { OpaqueEvidence = "not-a-trusted-receipt" });
        using var history = await client.GetAsync(
            $"/api/competitions/{competitionId}/me/history");
        using var adminCreate = await client.PostAsJsonAsync("/api/competitions",
            new { Name = "Contest", StartsAt = DateTimeOffset.UtcNow,
                EndsAt = DateTimeOffset.UtcNow.AddDays(1), MaximumPoints = 100 });

        Assert.Equal(HttpStatusCode.Unauthorized, registration.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, submission.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, history.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, adminCreate.StatusCode);
    }
}
