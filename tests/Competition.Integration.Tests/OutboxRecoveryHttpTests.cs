using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Competition.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;
using Xunit;

namespace Competition.Integration.Tests;

/// <summary>Validated JWT roles, actual HTTP routing, and PostgreSQL recovery audit.</summary>
public sealed class OutboxRecoveryHttpTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private static readonly byte[] Key = Enumerable.Repeat((byte)64, 32).ToArray();
    private const string Issuer = "https://test-admin-issuer.example";
    private const string Audience = "sample-api";
    private string _connection = "";

    private sealed class ApiFactory(string connection, bool recoveryEnabled) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Competitions"] = connection,
                    ["Authentication:Authority"] = Issuer,
                    ["Authentication:Audience"] = Audience,
                    ["TrustedAuthority:SigningKeyBase64"] =
                        Convert.ToBase64String(Enumerable.Repeat((byte)22, 32).ToArray()),
                    ["Outbox:RecoveryEnabled"] = recoveryEnabled.ToString(),
                    ["Outbox:Enabled"] = "false",
                    ["Inbox:Enabled"] = "false"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme,
                    options =>
                    {
                        options.Authority = null;
                        options.MetadataAddress = string.Empty;
                        options.MapInboundClaims = false;
                        options.TokenValidationParameters = new TokenValidationParameters
                        {
                            ValidateIssuer = true,
                            ValidIssuer = Issuer,
                            ValidateAudience = true,
                            ValidAudience = Audience,
                            ValidateLifetime = true,
                            ValidateIssuerSigningKey = true,
                            IssuerSigningKey = new SymmetricSecurityKey(Key),
                            RoleClaimType = "role",
                            NameClaimType = "sub"
                        };
                    });
            });
        }
    }

    public async Task InitializeAsync()
    {
        await _database.StartAsync();
        _connection = _database.GetConnectionString();
        var options = new DbContextOptionsBuilder<CompetitionDbContext>().UseNpgsql(_connection).Options;
        await using var db = new CompetitionDbContext(options);
        await db.Database.MigrateAsync();
    }
    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    private static string Token(bool admin)
    {
        var claims = new List<Claim> { new("sub", "operator-001") };
        if (admin) claims.Add(new Claim("role", "competition-admin"));
        var jwt = new JwtSecurityToken(Issuer, Audience, claims,
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddHours(1),
            new SigningCredentials(new SymmetricSecurityKey(Key), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private static void Authenticate(HttpClient client, bool admin) =>
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", Token(admin));

    private async Task<Guid> SeedDeadLetterAsync()
    {
        var id = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<CompetitionDbContext>()
            .UseNpgsql(_connection).Options;
        await using var db = new CompetitionDbContext(options);
        db.PendingEvents.Add(new OutboxRow
        {
            Id = id, CompetitionId = Guid.NewGuid(),
            EventType = "competition.result.accepted.v1",
            Payload = "{\"SchemaVersion\":1}",
            OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            DeadLetteredAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            AttemptCount = 5, LastFailureCode = "delivery.remote_error"
        });
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task Only_validated_admin_can_requeue_once_with_durable_reason_and_identity()
    {
        var eventId = await SeedDeadLetterAsync();
        await using var factory = new ApiFactory(_connection, true);
        using var client = factory.CreateClient();
        var url = $"/api/operations/outbox/{eventId}/redrive";

        using (var anonymous = await client.PostAsJsonAsync(url,
                   new { Reason = "Destination has recovered successfully" }))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        Authenticate(client, false);
        using (var player = await client.PostAsJsonAsync(url,
                   new { Reason = "Destination has recovered successfully" }))
            Assert.Equal(HttpStatusCode.Forbidden, player.StatusCode);

        Authenticate(client, true);
        using (var missing = await client.PostAsJsonAsync(url, new { Reason = "short" }))
            Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);

        using (var authorized = await client.PostAsJsonAsync(url,
                   new { Reason = "Operator inspected and repaired the remote destination" }))
            Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);

        using (var again = await client.PostAsJsonAsync(url,
                   new { Reason = "Operator inspected and repaired the remote destination" }))
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var options = new DbContextOptionsBuilder<CompetitionDbContext>()
            .UseNpgsql(_connection).Options;
        await using var db = new CompetitionDbContext(options);
        var audit = Assert.Single(await db.OutboxRecoveryAudits.AsNoTracking().ToListAsync());
        Assert.Equal(eventId, audit.EventId);
        Assert.NotEqual(Guid.Empty, audit.AdministratorId);
        Assert.Equal(5, audit.PreviousAttempts);
        Assert.Equal("Operator inspected and repaired the remote destination", audit.Reason);
        var job = await db.PendingEvents.AsNoTracking().SingleAsync(x => x.Id == eventId);
        Assert.Null(job.DeadLetteredAt);
        Assert.Equal(0, job.AttemptCount);
    }

    [Fact]
    public async Task Disabled_recovery_endpoint_cannot_reset_a_dead_letter()
    {
        var eventId = await SeedDeadLetterAsync();
        await using var factory = new ApiFactory(_connection, false);
        using var client = factory.CreateClient();
        Authenticate(client, true);
        using var response = await client.PostAsJsonAsync(
            $"/api/operations/outbox/{eventId}/redrive",
            new { Reason = "Attempt while recovery control is disabled" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var options = new DbContextOptionsBuilder<CompetitionDbContext>()
            .UseNpgsql(_connection).Options;
        await using var db = new CompetitionDbContext(options);
        Assert.Empty(await db.OutboxRecoveryAudits.ToListAsync());
        Assert.NotNull((await db.PendingEvents.SingleAsync(x => x.Id == eventId)).DeadLetteredAt);
    }
}
