using Competition.Web;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Competition.Adapters.Rest;
using Competition.Application.Ports;
using Competition.Application.UseCases;
using Competition.Infrastructure.Persistence;
using Competition.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// This is the outermost composition root. No application/domain class depends on this host.
builder.Services.AddScoped<ICompetitionGateway, PostgresCompetitionGateway>();
builder.Services.AddScoped<IEnrollmentGateway, PostgresEnrollmentGateway>();
builder.Services.AddScoped<IResultGateway, PostgresResultGateway>();
builder.Services.AddScoped<IResultUuidSource, RandomUuidSource>();
builder.Services.AddSingleton<IUtcClock, SystemUtcClock>();
builder.Services.AddScoped<ICreateCompetitionInputBoundary, CreateCompetitionInteractor>();
builder.Services.AddScoped<IRegisterParticipantInputBoundary, RegisterParticipantInteractor>();
builder.Services.AddScoped<ISubmitResultInputBoundary, SubmitResultInteractor>();
builder.Services.AddScoped<IGetLeaderboardInputBoundary, GetLeaderboardInteractor>();
builder.Services.AddScoped<IGetPersonalHistoryInputBoundary, GetPersonalHistoryInteractor>();
builder.Services.AddScoped<CompetitionController>();

var connectionString = builder.Configuration.GetConnectionString("Competitions");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("Set ConnectionStrings__Competitions in the environment.");
builder.Services.AddDbContext<CompetitionDbContext>(o => o.UseNpgsql(connectionString));

var authorityKey = builder.Configuration["TrustedAuthority:SigningKeyBase64"];
if (string.IsNullOrWhiteSpace(authorityKey))
    throw new InvalidOperationException("Set TrustedAuthority__SigningKeyBase64 outside source control.");
var secret = Convert.FromBase64String(authorityKey);
if (secret.Length < 32)
    throw new InvalidOperationException("Trusted authority key must have at least 256 bits.");
builder.Services.AddScoped<IResultEvidencePort>(sp =>
    new HmacResultEvidencePort(secret, sp.GetRequiredService<IUtcClock>()));

var jwtAuthority = builder.Configuration["Authentication:Authority"];
var jwtAudience = builder.Configuration["Authentication:Audience"];
if (string.IsNullOrWhiteSpace(jwtAuthority) || string.IsNullOrWhiteSpace(jwtAudience))
    throw new InvalidOperationException("Configure OIDC Authentication__Authority and Authentication__Audience.");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.Authority = jwtAuthority;
    options.Audience = jwtAudience;
    options.RequireHttpsMetadata = true;
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
        context.User.FindFirst("sub")?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});
builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler(handler =>
    handler.Run(async context =>
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("ApplicationFailure");
        logger.LogError("Request failed. TraceId={TraceId}", context.TraceIdentifier);
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

static IResult ToHttp(ApiViewModel response) =>
    Results.Json(new { code = response.Code, data = response.Data }, statusCode: response.StatusCode);

public sealed record CreateCompetitionHttp(string Name, DateTimeOffset StartsAt, DateTimeOffset EndsAt, int MaximumPoints);
public sealed record SubmitResultHttp(string OpaqueEvidence);
public partial class Program;
