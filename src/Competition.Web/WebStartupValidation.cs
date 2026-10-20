namespace Competition.Web;

/// <summary>
/// Fail-fast startup checks run only after all configuration providers, including test
/// overrides and deployment secrets, have been combined. No credentials are logged.
/// </summary>
public sealed class WebStartupValidation(IConfiguration configuration) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("Competitions")))
            throw new InvalidOperationException("Database connection must be configured.");

        if (string.IsNullOrWhiteSpace(configuration["Authentication:Authority"])
            || string.IsNullOrWhiteSpace(configuration["Authentication:Audience"]))
            throw new InvalidOperationException("OIDC authority and audience must be configured.");

        _ = ReadSigningKey(configuration);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public static byte[] ReadSigningKey(IConfiguration configuration)
    {
        var encoded = configuration["TrustedAuthority:SigningKeyBase64"];
        if (string.IsNullOrWhiteSpace(encoded))
            throw new InvalidOperationException("Trusted authority signing key must be configured.");

        byte[] secret;
        try
        {
            secret = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Trusted authority signing key is not valid Base64.");
        }

        if (secret.Length < 32)
            throw new InvalidOperationException("Trusted authority signing key requires at least 256 bits.");

        return secret;
    }
}
