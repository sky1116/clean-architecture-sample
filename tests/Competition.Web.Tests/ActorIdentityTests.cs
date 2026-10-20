using System.Security.Claims;
using Competition.Web;
using Xunit;

namespace Competition.Web.Tests;

public sealed class ActorIdentityTests
{
    private static ClaimsPrincipal Identity(string subject, string issuer) =>
        new(new ClaimsIdentity(new[] {
            new Claim("sub", subject), new Claim("iss", issuer)
        }, "validated-jwt"));

    [Fact]
    public void Same_validated_issuer_subject_produces_stable_actor_id()
    {
        var first = ActorIdentity.FromPrincipal(Identity("user-42", "https://issuer.example"));
        var again = ActorIdentity.FromPrincipal(Identity("user-42", "https://issuer.example"));
        Assert.NotEqual(Guid.Empty, first);
        Assert.Equal(first, again);
    }

    [Fact]
    public void Distinct_issuers_prevent_subject_collision()
    {
        Assert.NotEqual(
            ActorIdentity.FromPrincipal(Identity("user-42", "https://issuer-a.example")),
            ActorIdentity.FromPrincipal(Identity("user-42", "https://issuer-b.example")));
    }

    [Fact]
    public void Missing_authentication_or_issuer_must_not_map_to_a_person()
    {
        var unauthenticated = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("sub", "user-42"), new Claim("iss", "https://issuer.example") }));
        Assert.Equal(Guid.Empty, ActorIdentity.FromPrincipal(unauthenticated));
        var missingIssuer = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("sub", "user-42") }, "validated-jwt"));
        Assert.Equal(Guid.Empty, ActorIdentity.FromPrincipal(missingIssuer));
    }
}
