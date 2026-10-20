using System.Net;
using System.Security.Claims;
using Competition.Web;
using Xunit;

namespace Competition.Web.Tests;

public sealed class RequestRateLimitKeyTests
{
    private static ClaimsPrincipal Principal(string issuer) => new(
        new ClaimsIdentity(new[] {
            new Claim("sub", "same-subject"), new Claim("iss", issuer)
        }, "validated"));

    [Fact]
    public void Rate_limit_partitions_do_not_collide_across_validated_issuers()
    {
        var a = RequestRateLimitKey.From(Principal("https://issuer-one.example"),
            IPAddress.Loopback);
        var b = RequestRateLimitKey.From(Principal("https://issuer-two.example"),
            IPAddress.Loopback);
        Assert.NotEqual(a, b);
        Assert.Equal(a, RequestRateLimitKey.From(Principal("https://issuer-one.example"),
            IPAddress.Parse("192.0.2.1")));
        Assert.DoesNotContain("same-subject", a);
    }

    [Fact]
    public void Unauthenticated_requests_are_partitioned_without_trusting_claimed_subject()
    {
        var forged = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("sub", "same-subject") }));
        var a = RequestRateLimitKey.From(forged, IPAddress.Parse("192.0.2.1"));
        var b = RequestRateLimitKey.From(forged, IPAddress.Parse("192.0.2.2"));
        Assert.NotEqual(a, b);
        Assert.StartsWith("anonymous:", a);
    }
}
