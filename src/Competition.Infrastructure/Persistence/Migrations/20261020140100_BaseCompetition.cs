using Competition.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Competition.Infrastructure.Persistence.Migrations;

/// <summary>Versioned base schema. Does not contain transport or HTTP concerns.</summary>
[DbContext(typeof(CompetitionDbContext))]
[Migration("20261020140100_BaseCompetition")]
public sealed class BaseCompetition : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Competitions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                EndsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                MaximumPoints = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Competitions", x => x.Id));

        migrationBuilder.CreateTable(
            name: "Enrollments",
            columns: table => new
            {
                CompetitionId = table.Column<Guid>(type: "uuid", nullable: false),
                ParticipantId = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Enrollments",
                x => new { x.CompetitionId, x.ParticipantId }));

        migrationBuilder.CreateTable(
            name: "Results",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CompetitionId = table.Column<Guid>(type: "uuid", nullable: false),
                ParticipantId = table.Column<Guid>(type: "uuid", nullable: false),
                Points = table.Column<int>(type: "integer", nullable: false),
                EvidenceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Results", x => x.Id));

        migrationBuilder.CreateTable(
            name: "RequestReceipts",
            columns: table => new
            {
                CompetitionId = table.Column<Guid>(type: "uuid", nullable: false),
                ParticipantId = table.Column<Guid>(type: "uuid", nullable: false),
                IdempotencyKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ResultId = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_RequestReceipts",
                x => new { x.CompetitionId, x.ParticipantId, x.IdempotencyKey }));

        migrationBuilder.CreateTable(
            name: "AuditEntries",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CompetitionId = table.Column<Guid>(type: "uuid", nullable: false),
                ParticipantId = table.Column<Guid>(type: "uuid", nullable: false),
                ResultId = table.Column<Guid>(type: "uuid", nullable: false),
                Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_AuditEntries", x => x.Id));

        migrationBuilder.CreateTable(
            name: "Corrections",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CompetitionId = table.Column<Guid>(type: "uuid", nullable: false),
                ResultId = table.Column<Guid>(type: "uuid", nullable: false),
                AdministratorId = table.Column<Guid>(type: "uuid", nullable: false),
                Revision = table.Column<int>(type: "integer", nullable: false),
                ReplacementPoints = table.Column<int>(type: "integer", nullable: false),
                Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Corrections", x => x.Id));

        migrationBuilder.CreateIndex("IX_Enrollments_CompetitionId", "Enrollments", "CompetitionId");
        migrationBuilder.CreateIndex("IX_Results_CompetitionId_EvidenceId", "Results",
            new[] { "CompetitionId", "EvidenceId" }, unique: true);
        migrationBuilder.CreateIndex("IX_Results_CompetitionId_ParticipantId_RecordedAt", "Results",
            new[] { "CompetitionId", "ParticipantId", "RecordedAt" });
        migrationBuilder.CreateIndex("IX_AuditEntries_CompetitionId_OccurredAt", "AuditEntries",
            new[] { "CompetitionId", "OccurredAt" });
        migrationBuilder.CreateIndex("IX_Corrections_ResultId_Revision", "Corrections",
            new[] { "ResultId", "Revision" }, unique: true);
        migrationBuilder.CreateIndex("IX_Corrections_CompetitionId_OccurredAt", "Corrections",
            new[] { "CompetitionId", "OccurredAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("Corrections");
        migrationBuilder.DropTable("AuditEntries");
        migrationBuilder.DropTable("RequestReceipts");
        migrationBuilder.DropTable("Results");
        migrationBuilder.DropTable("Enrollments");
        migrationBuilder.DropTable("Competitions");
    }
}
