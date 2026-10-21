using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Competition.Infrastructure.Delivery;
using Competition.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;
using Xunit;

namespace Competition.Integration.Tests;

/// <summary>Full signature -> JSON adapter -> application -> PostgreSQL pipeline.</summary>
public sealed class InboxHttpTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private static readonly byte[] Key = Enumerable.Repeat((byte)91, 32).ToArray();
    private string _connection = "";

    private sealed class ApiFactory(string connection, bool enabled) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Competitions"] = connection,
                    ["Authentication:Authority"] = "https://identity.example",
                    ["Authentication:Audience"] = "sample-api",
                    ["TrustedAuthority:SigningKeyBase64"] =
                        Convert.ToBase64String(Enumerable.Repeat((byte)27, 32).ToArray()),
                    ["Inbox:Enabled"] = enabled.ToString(),
                    ["Inbox:WebhookSigningKeyBase64"] = Convert.ToBase64String(Key),
                    ["Outbox:Enabled"] = "false",
                    ["Database:BootstrapOnStart"] = "false"
                }));
        }
    }

    public async Task InitializeAsync()
    {
        await _database.StartAsync();
        _connection = _database.GetConnectionString();
        var options = new DbContextOptionsBuilder<CompetitionDbContext>()
            .UseNpgsql(_connection).Options;
        await using var db = new CompetitionDbContext(options);
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    private static byte[] Accepted(Guid eventId, Guid competitionId, Guid resultId, Guid playerId,
        int points) => JsonSerializer.SerializeToUtf8Bytes(new
        {
            EventId = eventId, CompetitionId = competitionId,
            EventType = "competition.result.accepted.v1",
            Payload = new { SchemaVersion = 1, CompetitionId = competitionId,
                ResultId = resultId, ParticipantId = playerId, Points = points }
        });

    private static byte[] Corrected(Guid eventId, Guid competitionId, Guid resultId,
        int revision, int points) => JsonSerializer.SerializeToUtf8Bytes(new
        {
            EventId = eventId, CompetitionId = competitionId,
            EventType = "competition.result.corrected.v1",
            Payload = new { SchemaVersion = 1, CompetitionId = competitionId,
                ResultId = resultId, Revision = revision, ReplacementPoints = points }
        });

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, byte[] body,
        Guid eventId, string type, bool validSignature = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/integrations/result-events");
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("X-Event-Id", eventId.ToString("D"));
        request.Headers.TryAddWithoutValidation("X-Event-Type", type);
        request.Headers.TryAddWithoutValidation("X-Webhook-Signature",
            WebhookSignature.Create(body, validSignature ? Key : new byte[32]));
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Signed_repeated_event_and_corrections_are_persisted_only_once()
    {
        await using var factory = new ApiFactory(_connection, true);
        using var client = factory.CreateClient();
        var eventId = Guid.NewGuid();
        var competition = Guid.NewGuid();
        var result = Guid.NewGuid();
        var participant = Guid.NewGuid();
        var original = Accepted(eventId, competition, result, participant, 80);

        using (var first = await SendAsync(client, original, eventId, "competition.result.accepted.v1"))
            Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        using (var again = await SendAsync(client, original, eventId, "competition.result.accepted.v1"))
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        // A forged change with the same ID, even if signed with a privileged test key,
        // must not be treated as the same idempotent request.
        var tampered = Accepted(eventId, competition, result, participant, 99);
        using (var conflict = await SendAsync(client, tampered, eventId, "competition.result.accepted.v1"))
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var correctionId = Guid.NewGuid();
        var correction = Corrected(correctionId, competition, result, 1, 35);
        using (var applied = await SendAsync(client, correction, correctionId, "competition.result.corrected.v1"))
            Assert.Equal(HttpStatusCode.Accepted, applied.StatusCode);
        using (var duplicate = await SendAsync(client, correction, correctionId, "competition.result.corrected.v1"))
            Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);

        var options = new DbContextOptionsBuilder<CompetitionDbContext>().UseNpgsql(_connection).Options;
        await using var db = new CompetitionDbContext(options);
        Assert.Equal(2, await db.InboxReceipts.CountAsync());
        var effective = Assert.Single(await db.ReceivedResults.ToListAsync());
        Assert.Equal(35, effective.EffectivePoints);
        Assert.Equal(1, effective.Revision);
        Assert.Empty(await db.Results.ToListAsync());
    }

    [Fact]
    public async Task Invalid_signature_and_mismatched_header_reject_before_database_write()
    {
        await using var factory = new ApiFactory(_connection, true);
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();
        var json = Accepted(id, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 45);

        using (var invalid = await SendAsync(client, json, id,
                   "competition.result.accepted.v1", validSignature: false))
            Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        using (var wrongType = await SendAsync(client, json, id,
                   "competition.result.corrected.v1"))
            Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongType.StatusCode);

        var options = new DbContextOptionsBuilder<CompetitionDbContext>().UseNpgsql(_connection).Options;
        await using var db = new CompetitionDbContext(options);
        Assert.Empty(await db.InboxReceipts.ToListAsync());
        Assert.Empty(await db.ReceivedResults.ToListAsync());
    }

    [Fact]
    public async Task Out_of_order_correction_returns_retryable_error_and_does_not_acknowledge()
    {
        await using var factory = new ApiFactory(_connection, true);
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();
        var competition = Guid.NewGuid();
        var result = Guid.NewGuid();
        var correction = Corrected(id, competition, result, 1, 30);
        using var response = await SendAsync(client, correction, id,
            "competition.result.corrected.v1");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var options = new DbContextOptionsBuilder<CompetitionDbContext>().UseNpgsql(_connection).Options;
        await using var db = new CompetitionDbContext(options);
        Assert.Empty(await db.InboxReceipts.ToListAsync());
    }

    [Fact]
    public async Task Inbox_endpoint_is_not_exposed_when_feature_is_disabled()
    {
        await using var factory = new ApiFactory(_connection, false);
        using var client = factory.CreateClient();
        var id = Guid.NewGuid();
        var payload = Accepted(id, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 10);
        using var response = await SendAsync(client, payload, id,
            "competition.result.accepted.v1");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Oversized_requests_are_rejected_before_signature_or_parsing()
    {
        await using var factory = new ApiFactory(_connection, true);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/integrations/result-events");
        request.Content = new ByteArrayContent(Enumerable.Repeat((byte)'a', 8193).ToArray());
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }
    [Theory]
    [InlineData("application/json-not-really")]
    [InlineData("text/plain")]
    [InlineData("application/octet-stream")]
    public async Task Non_json_content_types_are_rejected_before_a_database_write(string contentType)
    {
        await using var factory = new ApiFactory(_connection, true);
        using var client = factory.CreateClient();
        var eventId = Guid.NewGuid();
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/api/integrations/result-events");
        request.Content = new ByteArrayContent(Accepted(
            eventId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 42));
        request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);

        var options = new DbContextOptionsBuilder<CompetitionDbContext>()
            .UseNpgsql(_connection).Options;
        await using var db = new CompetitionDbContext(options);
        Assert.Empty(await db.InboxReceipts.ToListAsync());
        Assert.Empty(await db.ReceivedResults.ToListAsync());
    }

    [Fact]
    public async Task Oversized_chunked_upload_without_content_length_is_rejected()
    {
        await using var factory = new ApiFactory(_connection, true);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/api/integrations/result-events");
        request.Content = new StreamContent(new MemoryStream(
            Enumerable.Repeat((byte)'x', 8193).ToArray()));
        request.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/json");
        request.Headers.TransferEncodingChunked = true;
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);

        var options = new DbContextOptionsBuilder<CompetitionDbContext>()
            .UseNpgsql(_connection).Options;
        await using var db = new CompetitionDbContext(options);
        Assert.Empty(await db.InboxReceipts.ToListAsync());
    }

}
