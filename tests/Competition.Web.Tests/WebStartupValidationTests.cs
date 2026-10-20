using Microsoft.Extensions.Configuration;
using Competition.Web;
using Xunit;

namespace Competition.Web.Tests;

public sealed class WebStartupValidationTests
{
    private static IConfiguration Build(bool db = true, bool identity = true, string? key = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Competitions"] = db ? "Host=localhost;Database=sample" : null,
            ["Authentication:Authority"] = identity ? "https://issuer.example" : null,
            ["Authentication:Audience"] = identity ? "sample-api" : null,
            ["TrustedAuthority:SigningKeyBase64"] =
                key ?? Convert.ToBase64String(new byte[32])
        };
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public async Task Startup_refuses_missing_database_configuration()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WebStartupValidation(Build(db: false)).StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Startup_refuses_missing_identity_provider_configuration()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WebStartupValidation(Build(identity: false)).StartAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("AA==")]
    public async Task Startup_refuses_malformed_or_weak_evidence_key_without_disclosing_it(string secret)
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WebStartupValidation(Build(key: secret)).StartAsync(CancellationToken.None));
        Assert.DoesNotContain(secret, exception.Message);
    }

    [Fact]
    public async Task Explicit_complete_configuration_can_start_without_connecting_to_database()
    {
        await new WebStartupValidation(Build()).StartAsync(CancellationToken.None);
    }
}
