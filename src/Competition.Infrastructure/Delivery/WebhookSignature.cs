using System.Security.Cryptography;

namespace Competition.Infrastructure.Delivery;

/// <summary>
/// Transport authentication, not competition/domain policy.
/// Shared secret must be provisioned separately on sender and receiver.
/// </summary>
public static class WebhookSignature
{
    public static string Create(ReadOnlySpan<byte> payload, byte[] key)
    {
        if (key is null || key.Length < 32)
            throw new ArgumentException("Webhook key must have at least 256 bits.", nameof(key));
        return "v1=" + Convert.ToBase64String(HMACSHA256.HashData(key, payload));
    }

    public static bool Verify(ReadOnlySpan<byte> payload, string? signature, byte[] key)
    {
        if (key is null || key.Length < 32 || string.IsNullOrEmpty(signature)
            || !signature.StartsWith("v1=", StringComparison.Ordinal))
            return false;
        var encoded = signature[3..];
        if (encoded.Length != 44) return false;
        try
        {
            var incoming = Convert.FromBase64String(encoded);
            var expected = HMACSHA256.HashData(key, payload);
            return incoming.Length == expected.Length &&
                   CryptographicOperations.FixedTimeEquals(incoming, expected);
        }
        catch (FormatException) { return false; }
    }

    public static byte[] ReadKey(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
            throw new InvalidOperationException("Configured webhook key is missing.");
        try
        {
            var key = Convert.FromBase64String(base64);
            if (key.Length >= 32) return key;
        }
        catch (FormatException) { }
        throw new InvalidOperationException("Configured webhook key is invalid or too short.");
    }
}
