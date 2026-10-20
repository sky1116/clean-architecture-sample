using Microsoft.EntityFrameworkCore;

namespace Competition.Infrastructure.Persistence;

/// <summary>Persistence models never escape the infrastructure circle.</summary>
public sealed class CompetitionDbContext(DbContextOptions<CompetitionDbContext> options) : DbContext(options)
{
    public DbSet<CompetitionRow> Competitions => Set<CompetitionRow>();
    public DbSet<EnrollmentRow> Enrollments => Set<EnrollmentRow>();
    public DbSet<ResultRow> Results => Set<ResultRow>();
    public DbSet<RequestReceiptRow> RequestReceipts => Set<RequestReceiptRow>();
    public DbSet<AuditRow> AuditEntries => Set<AuditRow>();
    public DbSet<CorrectionRow> Corrections => Set<CorrectionRow>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<CompetitionRow>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Name).HasMaxLength(100).IsRequired();
        });
        model.Entity<EnrollmentRow>(builder =>
        {
            builder.HasKey(x => new { x.CompetitionId, x.ParticipantId });
            builder.HasIndex(x => x.CompetitionId);
        });
        model.Entity<ResultRow>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.HasIndex(x => new { x.CompetitionId, x.EvidenceId }).IsUnique();
            builder.Property(x => x.EvidenceId).HasMaxLength(128).IsRequired();
            builder.HasIndex(x => new { x.CompetitionId, x.ParticipantId, x.RecordedAt });
        });
        model.Entity<RequestReceiptRow>(builder =>
        {
            builder.HasKey(x => new { x.CompetitionId, x.ParticipantId, x.IdempotencyKey });
            builder.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
            builder.Property(x => x.Fingerprint).HasMaxLength(64).IsRequired();
        });
        model.Entity<CorrectionRow>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.HasIndex(x => new { x.ResultId, x.Revision }).IsUnique();
            builder.HasIndex(x => new { x.CompetitionId, x.OccurredAt });
            builder.Property(x => x.Reason).HasMaxLength(500).IsRequired();
        });
        model.Entity<AuditRow>(builder =>
        {
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Action).HasMaxLength(64).IsRequired();
            builder.HasIndex(x => new { x.CompetitionId, x.OccurredAt });
        });
    }
}
public sealed class CompetitionRow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public int MaximumPoints { get; set; }
}
public sealed class EnrollmentRow
{
    public Guid CompetitionId { get; set; }
    public Guid ParticipantId { get; set; }
}
public sealed class ResultRow
{
    public Guid Id { get; set; }
    public Guid CompetitionId { get; set; }
    public Guid ParticipantId { get; set; }
    public int Points { get; set; }
    public string EvidenceId { get; set; } = "";
    public DateTimeOffset RecordedAt { get; set; }
}
public sealed class RequestReceiptRow
{
    public Guid CompetitionId { get; set; }
    public Guid ParticipantId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public Guid ResultId { get; set; }
}
public sealed class AuditRow
{
    public Guid Id { get; set; }
    public Guid CompetitionId { get; set; }
    public Guid ParticipantId { get; set; }
    public Guid ResultId { get; set; }
    public string Action { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class CorrectionRow
{
    public Guid Id { get; set; }
    public Guid CompetitionId { get; set; }
    public Guid ResultId { get; set; }
    public Guid AdministratorId { get; set; }
    public int Revision { get; set; }
    public int ReplacementPoints { get; set; }
    public string Reason { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
}
