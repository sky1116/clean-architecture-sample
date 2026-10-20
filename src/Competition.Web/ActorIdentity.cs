using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Competition.Web;

/// <summary>Map a validated issuer+subject pair to a stable internal identifier.
/// Never derive an actor from a client-controlled request field.</summary>
public static class ActorIdentity
{
    public static Guid FromPrincipal(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
            return Guid.Empty;
        var subject = principal.FindFirst("sub")?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var issuer = principal.FindFirst("iss")?.Value;
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(issuer))
            return Guid.Empty;
        var fingerprint = SHA256.HashData(Encoding.UTF8.GetBytes(issuer + "\0" + subject));
        var bytes = fingerprint.AsSpan(0, 16).ToArray();
        // Stable namespace-like UUID derived only from issuer-validated identity claims.
        bytes[7] = (byte)((bytes[7] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes);
    }
}
