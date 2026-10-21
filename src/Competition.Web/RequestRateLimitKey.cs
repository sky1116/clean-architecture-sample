using System.Net;
using System.Security.Claims;

namespace Competition.Web;

/// <summary>Partitions by validated issuer + subject, not a potentially ambiguous raw sub claim.</summary>
public static class RequestRateLimitKey
{
    public static string From(ClaimsPrincipal principal, IPAddress? remoteAddress)
    {
        var actor = ActorIdentity.FromPrincipal(principal);
        return actor == Guid.Empty
            ? "anonymous:" + (remoteAddress?.ToString() ?? "unknown")
            : "authenticated:" + actor.ToString("N");
    }
}
