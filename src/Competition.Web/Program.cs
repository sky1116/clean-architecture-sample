using Competition.Web;
using System.Security.Claims;
using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using System.Threading.RateLimiting;
using Competition.Adapters.Rest;
using Competition.Application.Ports;
using Competition.Application.UseCases;
using Competition.Infrastructure.Persistence;
using Competition.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// This is the outermost composition root. No application/domain class depends on this host.
builder.Services.AddScoped<ICompetitionGateway, PostgresCompetitionGateway>();
builder.Services.AddScoped<IEnrollmentGateway, PostgresEnrollmentGateway>();
builder.Services.AddScoped<IResultGateway, PostgresResultGateway>();
builder.Services.AddScoped<IResultCorrectionPort, PostgresCorrectionGateway>();
builder.Services.AddScoped<IParticipantHistoryPort, PostgresParticipantHistoryGateway>();
builder.Services.AddScoped<IResultUuidSource, RandomUuidSource>();
builder.Services.AddSingleton<IUtcClock, SystemUtcClock>();
builder.Services.AddScoped<ICreateCompetitionInputBoundary, CreateCompetitionInteractor>();
builder.Services.AddScoped<IRegisterParticipantInputBoundary, RegisterParticipantInteractor>();
builder.Services.AddScoped<ISubmitResultInputBoundary, SubmitResultInteractor>();
builder.Services.AddScoped<ICorrectResultInputBoundary, CorrectResultInteractor>();
builder.Services.AddScoped<IGetLeaderboardInputBoundary, GetLeaderboardInteractor>();
builder.Services.AddScoped<IGetPersonalHistoryInputBoundary, GetPersonalHistoryInteractor>();
builder.Services.AddScoped<CompetitionController>();

builder.Services.AddDbContext<CompetitionDbContext>((services, options) =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    options.UseNpgsql(configuration.GetConnectionString("Competitions")
        ?? throw new InvalidOperationException("Database connection must be configured."));
});
builder.Services.AddScoped<IResultEvidencePort>(services =>
    new HmacResultEvidencePort(
        WebStartupValidation.ReadSigningKey(services.GetRequiredService<IConfiguration>()),
        services.GetRequiredService<IUtcClock>()));
// Verify final configuration only once the host has applied all configuration providers.
builder.Services.AddHostedService<WebStartupValidation>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.Authority = builder.Configuration["Authentication:Authority"];
    options.Audience = builder.Configuration["Authentication:Audience"];
    options.RequireHttpsMetadata = true;
    // Use the issuer of the already validated token, not a request-supplied identity hint.
    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = context =>
        {
            if (context.Principal?.Identity is ClaimsIdentity identity
                && context.SecurityToken is { Issuer: var issuer })
            {
                var existing = identity.FindFirst("iss");
                if (existing is not null) identity.RemoveClaim(existing);
                identity.AddClaim(new Claim("iss", issuer));
            }
            return Task.CompletedTask;
        }
    };
});
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("competition-admin", policy =>
        policy.RequireAuthenticatedUser().RequireRole("competition-admin"));
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("submissions", context => RateLimitPartition.GetFixedWindowLimiter(
        RequestRateLimitKey.From(context.User, context.Connection.RemoteIpAddress),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});
builder.Services.AddProblemDetails();

// External delivery is explicitly opt-in; a plain checkout does not call network endpoints.
// The target is configured by the operator, not by request data or an outbox payload.
if (builder.Configuration.GetValue<bool>("Outbox:Enabled"))
{
    var raw = builder.Configuration["Outbox:WebhookUrl"];
    if (!Uri.TryCreate(raw, UriKind.Absolute, out var endpoint)
        || endpoint.Scheme != Uri.UriSchemeHttps || endpoint.UserInfo.Length != 0
        || !string.IsNullOrEmpty(endpoint.Fragment))
        throw new InvalidOperationException("Enabled Outbox requires a secure HTTPS WebhookUrl.");
    builder.Services.AddScoped<IOutboxDeliveryStore, PostgresOutboxDeliveryStore>();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddHttpClient("outbox-delivery", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(10);
    })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
    builder.Services.AddSingleton<IOutboxEventSink>(services =>
        new HttpOutboxEventSink(services.GetRequiredService<IHttpClientFactory>()
            .CreateClient("outbox-delivery"), endpoint));
    builder.Services.AddHostedService<OutboxDispatchWorker>();
}


// All instrumentation stays at the outermost framework boundary.
// The collector is opt-in: a local checkout sends no telemetry over the network.
var exportOtlp = builder.Configuration.GetValue<bool>("Telemetry:ExportOtlp");
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("competition-platform"))
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation();
        if (exportOtlp) tracing.AddOtlpExporter();
    })
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation();
        if (exportOtlp) metrics.AddOtlpExporter();
    });

var app = builder.Build();
app.UseExceptionHandler(handler =>
    handler.Run(async context =>
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("ApplicationFailure");
        var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        logger.LogError("Request failed. TraceId={TraceId}, FailureType={FailureType}",
            context.TraceIdentifier, error?.GetType().Name);
        context.Response.StatusCode = 500;
        await Results.Problem(statusCode: 500, title: "Unexpected error",
            extensions: new Dictionary<string, object?> { ["traceId"] = context.TraceIdentifier })
            .ExecuteAsync(context);
    }));

if (app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("Database:BootstrapOnStart"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var database = scope.ServiceProvider.GetRequiredService<CompetitionDbContext>();
    // Demo-only schema setup. A production deployment requires versioned migrations.
    await database.Database.EnsureCreatedAsync();
}
else if (!app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("Database:BootstrapOnStart"))
{
    throw new InvalidOperationException("Automatic schema bootstrap is forbidden outside Development.");
}

// Structured completion logs contain only route patterns, status, duration and trace correlation.
app.Use(async (context, next) =>
{
    var clock = Stopwatch.StartNew();
    try { await next(); }
    finally
    {
        clock.Stop();
        var log = context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("HttpRequests");
        log.LogInformation(
            "RequestCompleted Method={Method} Route={Route} StatusCode={StatusCode} DurationMs={DurationMs} TraceId={TraceId}",
            context.Request.Method, context.GetEndpoint()?.DisplayName ?? "unmatched",
            context.Response.StatusCode, clock.Elapsed.TotalMilliseconds, Activity.Current?.TraceId.ToString());
    }
});
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapGet("/health", () => Results.Ok(new { status = "alive" }));

app.MapPost("/api/competitions", async (
    CreateCompetitionHttp request, CompetitionController controller, ClaimsPrincipal actor,
    CancellationToken ct) =>
{
    var input = new CreateCompetitionInput(Guid.NewGuid(), request.Name, request.StartsAt,
        request.EndsAt, request.MaximumPoints, actor.IsInRole("competition-admin"));
    var response = await controller.CreateAsync(input, ct);
    return ToHttp(response);
}).RequireAuthorization("competition-admin");

app.MapPost("/api/competitions/{competitionId:guid}/participants", async (
    Guid competitionId, CompetitionController controller, ClaimsPrincipal actor, CancellationToken ct) =>
{
    var response = await controller.RegisterAsync(new RegisterParticipantInput(
        competitionId, ActorIdentity.FromPrincipal(actor)), ct);
    return ToHttp(response);
}).RequireAuthorization();

app.MapPost("/api/competitions/{competitionId:guid}/results", async (
    Guid competitionId, SubmitResultHttp request, HttpContext context,
    CompetitionController controller, ClaimsPrincipal actor, CancellationToken ct) =>
{
    var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
    var response = await controller.SubmitAsync(new SubmitResultInput(
        competitionId, ActorIdentity.FromPrincipal(actor), idempotencyKey, request.OpaqueEvidence), ct);
    return ToHttp(response);
}).RequireAuthorization().RequireRateLimiting("submissions");

app.MapPost("/api/competitions/{competitionId:guid}/results/{resultId:guid}/corrections", async (
    Guid competitionId, Guid resultId, CorrectResultHttp request, CompetitionController controller,
    ClaimsPrincipal actor, CancellationToken ct) =>
{
    var response = await controller.CorrectAsync(new CorrectResultInput(
        competitionId, resultId, ActorIdentity.FromPrincipal(actor), actor.IsInRole("competition-admin"),
        request.ExpectedRevision, request.ReplacementPoints, request.Reason), ct);
    return ToHttp(response);
}).RequireAuthorization("competition-admin");

app.MapGet("/api/competitions/{competitionId:guid}/leaderboard", async (
    Guid competitionId, int? offset, int? limit, CompetitionController controller, CancellationToken ct) =>
{
    var response = await controller.LeaderboardAsync(new GetLeaderboardInput(
        competitionId, offset ?? 0, limit ?? 20), ct);
    return ToHttp(response);
});

app.MapGet("/api/competitions/{competitionId:guid}/me/history", async (
    Guid competitionId, int? offset, int? limit, CompetitionController controller,
    ClaimsPrincipal actor, CancellationToken ct) =>
{
    var response = await controller.HistoryAsync(new GetPersonalHistoryInput(
        competitionId, ActorIdentity.FromPrincipal(actor), offset ?? 0, limit ?? 20), ct);
    return ToHttp(response);
}).RequireAuthorization();

app.Run();

static IResult ToHttp(ApiViewModel response) => new HttpApiResult(response);

public sealed record CreateCompetitionHttp(string Name, DateTimeOffset StartsAt, DateTimeOffset EndsAt, int MaximumPoints);
public sealed record SubmitResultHttp(string OpaqueEvidence);
public sealed record CorrectResultHttp(int ExpectedRevision, int ReplacementPoints, string Reason);
public partial class Program;
