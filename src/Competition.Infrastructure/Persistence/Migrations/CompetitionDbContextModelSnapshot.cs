using Competition.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Competition.Infrastructure.Persistence.Migrations;

/// <summary>
/// Checked-in terminal EF model snapshot. Historical migrations MUST remain immutable.
/// If the model changes, add a new migration and regenerate/review this snapshot.
/// </summary>
[DbContext(typeof(CompetitionDbContext))]
public sealed class CompetitionDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.0")
            .HasAnnotation("Relational:MaxIdentifierLength", 63);

        modelBuilder.Entity<CompetitionRow>(entity =>
        {
            entity.Property<Guid>(nameof(CompetitionRow.Id)).ValueGeneratedOnAdd().HasColumnType("uuid");
            entity.Property<string>(nameof(CompetitionRow.Name)).IsRequired()
                .HasMaxLength(100).HasColumnType("character varying(100)");
            entity.Property<DateTimeOffset>(nameof(CompetitionRow.StartsAt)).HasColumnType("timestamp with time zone");
            entity.Property<DateTimeOffset>(nameof(CompetitionRow.EndsAt)).HasColumnType("timestamp with time zone");
            entity.Property<int>(nameof(CompetitionRow.MaximumPoints)).HasColumnType("integer");
            entity.HasKey(x => x.Id);
            entity.ToTable("Competitions");
        });

        modelBuilder.Entity<EnrollmentRow>(entity =>
        {
            entity.Property<Guid>(nameof(EnrollmentRow.CompetitionId)).HasColumnType("uuid");
            entity.Property<Guid>(nameof(EnrollmentRow.ParticipantId)).HasColumnType("uuid");
            entity.HasKey(x => new { x.CompetitionId, x.ParticipantId });
            entity.HasIndex(x => x.CompetitionId);
            entity.ToTable("Enrollments");
        });

        modelBuilder.Entity<ResultRow>(entity =>
        {
            entity.Property<Guid>(nameof(ResultRow.Id)).ValueGeneratedOnAdd().HasColumnType("uuid");
            entity.Property<Guid>(nameof(ResultRow.CompetitionId)).HasColumnType("uuid");
            entity.Property<Guid>(nameof(ResultRow.ParticipantId)).HasColumnType("uuid");
            entity.Property<int>(nameof(ResultRow.Points)).HasColumnType("integer");
            entity.Property<string>(nameof(ResultRow.EvidenceId)).IsRequired()
                .HasMaxLength(128).HasColumnType("character varying(128)");
            entity.Property<DateTimeOffset>(nameof(ResultRow.RecordedAt)).HasColumnType("timestamp with time zone");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.CompetitionId, x.EvidenceId }).IsUnique();
            entity.HasIndex(x => new { x.CompetitionId, x.ParticipantId, x.RecordedAt });
            entity.ToTable("Results");
        });

        modelBuilder.Entity<RequestReceiptRow>(entity =>
        {
            entity.Property<Guid>(nameof(RequestReceiptRow.CompetitionId)).HasColumnType("uuid");
            entity.Property<Guid>(nameof(RequestReceiptRow.ParticipantId)).HasColumnType("uuid");
            entity.Property<string>(nameof(RequestReceiptRow.IdempotencyKey)).IsRequired()
                .HasMaxLength(100).HasColumnType("character varying(100)");
            entity.Property<string>(nameof(RequestReceiptRow.Fingerprint)).IsRequired()
                .HasMaxLength(64).HasColumnType("character varying(64)");
            entity.Property<Guid>(nameof(RequestReceiptRow.ResultId)).HasColumnType("uuid");
            entity.HasKey(x => new { x.CompetitionId, x.ParticipantId, x.IdempotencyKey });
            entity.ToTable("RequestReceipts");
        });

        modelBuilder.Entity<AuditRow>(entity =>
        {
            entity.Property<Guid>(nameof(AuditRow.Id)).ValueGeneratedOnAdd().HasColumnType("uuid");
            entity.Property<Guid>(nameof(AuditRow.CompetitionId)).HasColumnType("uuid");
            entity.Property<Guid>(nameof(AuditRow.ParticipantId)).HasColumnType("uuid");
            entity.Property<Guid>(nameof(AuditRow.ResultId)).HasColumnType("uuid");
            entity.Property<string>(nameof(AuditRow.Action)).IsRequired()
                .HasMaxLength(64).HasColumnType("character varying(64)");
            entity.Property<DateTimeOffset>(nameof(AuditRow.OccurredAt)).HasColumnType("timestamp with time zone");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.CompetitionId, x.OccurredAt });
            entity.ToTable("AuditEntries");
        });

        modelBuilder.Entity<CorrectionRow>(entity =>
        {
            entity.Property<Guid>(nameof(CorrectionRow.Id)).ValueGeneratedOnAdd().HasColumnType("uuid");
            entity.Property<Guid>(nameof(CorrectionRow.CompetitionId)).HasColumnType("uuid");
            entity.Property<Guid>(nameof(CorrectionRow.ResultId)).HasColumnType("uuid");
            entity.Property<Guid>(nameof(CorrectionRow.AdministratorId)).HasColumnType("uuid");
            entity.Property<int>(nameof(CorrectionRow.Revision)).HasColumnType("integer");
            entity.Property<int>(nameof(CorrectionRow.ReplacementPoints)).HasColumnType("integer");
            entity.Property<string>(nameof(CorrectionRow.Reason)).IsRequired()
                .HasMaxLength(500).HasColumnType("character varying(500)");
            entity.Property<DateTimeOffset>(nameof(CorrectionRow.OccurredAt)).HasColumnType("timestamp with time zone");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ResultId, x.Revision }).IsUnique();
            entity.HasIndex(x => new { x.CompetitionId, x.OccurredAt });
            entity.ToTable("Corrections");
        });

        modelBuilder.Entity<InboxReceiptRow>(entity =>
        {
            entity.Property<Guid>(nameof(InboxReceiptRow.EventId)).ValueGeneratedOnAdd().HasColumnType("uuid");
            entity.Property<Guid>(nameof(InboxReceiptRow.CompetitionId)).HasColumnType("uuid");
            entity.Property<Guid>(nameof(InboxReceiptRow.ResultId)).HasColumnType("uuid");
            entity.Property<int>(nameof(InboxReceiptRow.Revision)).HasColumnType("integer");
            entity.Property<string>(nameof(InboxReceiptRow.EventType)).IsRequired()
                .HasMaxLength(100).HasColumnType("character varying(100)");
            entity.Property<string>(nameof(InboxReceiptRow.PayloadDigest)).IsRequired()
                .HasMaxLength(64).HasColumnType("character varying(64)");
            entity.Property<DateTimeOffset>(nameof(InboxReceiptRow.ReceivedAt))
                .HasColumnType("timestamp with time zone");
            entity.HasKey(x => x.EventId);
            entity.HasIndex(x => new { x.CompetitionId, x.ResultId, x.Revision }).IsUnique();
            entity.ToTable("InboxReceipts");
        });

        modelBuilder.Entity<ReceivedResultRow>(entity =>
        {
            entity.Property<Guid>(nameof(ReceivedResultRow.CompetitionId)).HasColumnType("uuid");
            entity.Property<Guid>(nameof(ReceivedResultRow.ResultId)).HasColumnType("uuid");
            entity.Property<Guid>(nameof(ReceivedResultRow.ParticipantId)).HasColumnType("uuid");
            entity.Property<int>(nameof(ReceivedResultRow.EffectivePoints)).HasColumnType("integer");
            entity.Property<int>(nameof(ReceivedResultRow.Revision)).HasColumnType("integer");
            entity.HasKey(x => new { x.CompetitionId, x.ResultId });
            entity.ToTable("ReceivedResults");
        });

        modelBuilder.Entity<OutboxRow>(entity =>
        {
            entity.Property<Guid>(nameof(OutboxRow.Id)).ValueGeneratedOnAdd().HasColumnType("uuid");
            entity.Property<Guid>(nameof(OutboxRow.CompetitionId)).HasColumnType("uuid");
            entity.Property<string>(nameof(OutboxRow.EventType)).IsRequired()
                .HasMaxLength(100).HasColumnType("character varying(100)");
            entity.Property<string>(nameof(OutboxRow.Payload)).IsRequired().HasColumnType("jsonb");
            entity.Property<DateTimeOffset>(nameof(OutboxRow.OccurredAt)).HasColumnType("timestamp with time zone");
            entity.Property<DateTimeOffset?>(nameof(OutboxRow.PublishedAt)).HasColumnType("timestamp with time zone");
            entity.Property<int>(nameof(OutboxRow.AttemptCount)).HasColumnType("integer");
            entity.Property<DateTimeOffset>(nameof(OutboxRow.NextAttemptAt)).HasColumnType("timestamp with time zone");
            entity.Property<DateTimeOffset?>(nameof(OutboxRow.LeaseUntil)).HasColumnType("timestamp with time zone");
            entity.Property<Guid?>(nameof(OutboxRow.LeaseToken)).HasColumnType("uuid");
            entity.Property<DateTimeOffset?>(nameof(OutboxRow.DeadLetteredAt)).HasColumnType("timestamp with time zone");
            entity.Property<string>(nameof(OutboxRow.LastFailureCode))
                .HasMaxLength(80).HasColumnType("character varying(80)");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.PublishedAt, x.DeadLetteredAt, x.NextAttemptAt });
            entity.ToTable("PendingEvents");
        });
    }
}
