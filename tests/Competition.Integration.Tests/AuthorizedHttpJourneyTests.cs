using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Competition.Application.Ports;
using Competition.Infrastructure.Persistence;
using Competition.Web;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using Testcontainers.PostgreSql;
using Xunit;

namespace Competition.Integration.Tests;

/// <summary>
/// Actual ASP.NET HTTP routing + cryptographically signed JWT + HMAC evidence + real
/// PostgreSQL. Test keys and identities are deterministic local-only fixtures.
/// </summary>
public sealed class AuthorizedHttpJourneyTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private static readonly byte[] JwtKey = Enumerable.Repeat((byte)17, 32).ToArray();
    private static readonly byte[] AuthorityKey = Enumerable.Repeat((byte)81, 32).ToArray();
    private const string Issuer = "https://test-identity.example";
    private const string Audience = "competition-api";
    private readonly MutableClock _clock = new();
    private string _connection = "";

    private sealed class MutableClock : IUtcClock
    {
        public DateTimeOffset Current { get; set; } =
            new(2027, 1, 1, 10, 0, 0, TimeSpan.Zero);
        public DateTimeOffset UtcNow => Current;
    }

    private sealed class ApiFactory(string connection, MutableClock clock) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Competitions"] = connection,
                    ["Authentication:Authority"] = Issuer,
                    ["Authentication:Audience"] = Audience,
                    ["TrustedAuthority:SigningKeyBase64"] = Convert.ToBase64String(AuthorityKey),
                    ["Database:BootstrapOnStart"] = "false",
                    ["Outbox:Enabled"] = "false"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IUtcClock>(clock);
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
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
                        IssuerSigningKey = new SymmetricSecurityKey(JwtKey),
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
        var options = new DbContextOptionsBuilder<CompetitionDbContext>()
            .UseNpgsql(_connection).Options;
        await using var context = new CompetitionDbContext(options);
        await context.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    private static string Token(string subject, bool admin = false)
    {
        var claims = new List<Claim> { new("sub", subject) };
        if (admin) claims.Add(new Claim("role", "competition-admin"));
        var jwt = new JwtSecurityToken(
            issuer: Issuer, audience: Audience, claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(JwtKey),
                SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private static HttpClient WithToken(HttpClient client, string subject, bool admin = false)
    {
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", Token(subject, admin));
        return client;
    }

    private string Receipt(Guid competition, Guid participant, int points, string evidenceId)
    {
        var now = _clock.UtcNow.ToUnixTimeSeconds();
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            CompetitionId = competition,
            ParticipantId = participant,
            Points = points,
            EvidenceId = evidenceId,
            IssuedAtUnix = now - 30,
            ExpiresAtUnix = now + 120
        });
        var encoded = Encode(body);
        var signature = HMACSHA256.HashData(AuthorityKey, Encoding.ASCII.GetBytes(encoded));
        return encoded + "." + Encode(signature);
    }

    private static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    [Fact]
    public async Task Full_competition_journey_preserves_identity_idempotency_rankings_and_history()
    {
        await using var factory = new ApiFactory(_connection, _clock);
        using var client = factory.CreateClient();
        WithToken(client, "admin-one", admin: true);
        var starts = _clock.UtcNow.AddMinutes(10);
        using var created = await client.PostAsJsonAsync("/api/competitions", new
        {
            Name = "Autumn Contest",
            StartsAt = starts,
            EndsAt = starts.AddHours(2),
            MaximumPoints = 100
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdData = await ReadJsonAsync(created);
        var id = createdData.RootElement.GetProperty("data").GetProperty("competitionId").GetGuid();

        WithToken(client, "player-one");
        using var registration = await client.PostAsJsonAsync(
            $"/api/competitions/{id}/participants", new { });
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);

        _clock.Current = starts.AddMinutes(1);
        var authenticatedActor = ActorIdentity.FromPrincipal(new ClaimsPrincipal(
            new ClaimsIdentity(new[]
            {
                new Claim("sub", "player-one"), new Claim("iss", Issuer)
            }, "validated")));
        var proof = Receipt(id, authenticatedActor, 78, "signed-result-001");
        async Task<HttpResponseMessage> SubmitAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"/api/competitions/{id}/results");
            request.Headers.TryAddWithoutValidation("Idempotency-Key", "same-submission-0001");
            request.Content = JsonContent.Create(new { OpaqueEvidence = proof });
            return await client.SendAsync(request);
        }

        using var accepted = await SubmitAsync();
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        using var acceptedData = await ReadJsonAsync(accepted);
        var resultId = acceptedData.RootElement.GetProperty("data").GetProperty("resultId").GetGuid();
        using var replay = await SubmitAsync();
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var replayData = await ReadJsonAsync(replay);
        Assert.Equal(resultId, replayData.RootElement.GetProperty("data").GetProperty("resultId").GetGuid());

        using var leaderboard = await client.GetAsync($"/api/competitions/{id}/leaderboard");
        Assert.Equal(HttpStatusCode.OK, leaderboard.StatusCode);
        using var rankingData = await ReadJsonAsync(leaderboard);
        var rankedPlayer = rankingData.RootElement.GetProperty("data").GetProperty("rows")[0];
        Assert.Equal(1, rankedPlayer.GetProperty("rank").GetInt32());
        Assert.Equal(78, rankedPlayer.GetProperty("totalPoints").GetInt64());

        using var history = await client.GetAsync($"/api/competitions/{id}/me/history");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        using var historyData = await ReadJsonAsync(history);
        Assert.Equal(1, historyData.RootElement.GetProperty("data").GetProperty("totalCount").GetInt32());

        WithToken(client, "admin-one", admin: true);
        using var corrected = await client.PostAsJsonAsync(
            $"/api/competitions/{id}/results/{resultId}/corrections", new
            {
                ExpectedRevision = 0,
                ReplacementPoints = 55,
                Reason = "Referee verified the disputed score"
            });
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);

        WithToken(client, "player-one");
        using var updated = await client.GetAsync($"/api/competitions/{id}/leaderboard");
        using var updatedData = await ReadJsonAsync(updated);
        Assert.Equal(55, updatedData.RootElement.GetProperty("data")
            .GetProperty("rows")[0].GetProperty("totalPoints").GetInt64());
        using var allHistory = await client.GetAsync($"/api/competitions/{id}/me/history");
        using var allData = await ReadJsonAsync(allHistory);
        Assert.Equal(2, allData.RootElement.GetProperty("data").GetProperty("totalCount").GetInt32());

        var options = new DbContextOptionsBuilder<CompetitionDbContext>().UseNpgsql(_connection).Options;
        await using var db = new CompetitionDbContext(options);
        Assert.Equal(1, await db.Results.CountAsync());
        Assert.Equal(1, await db.RequestReceipts.CountAsync());
        Assert.Equal(2, await db.AuditEntries.CountAsync());
        Assert.Equal(2, await db.PendingEvents.CountAsync());
        Assert.Equal(1, await db.Corrections.CountAsync());
    }
}
