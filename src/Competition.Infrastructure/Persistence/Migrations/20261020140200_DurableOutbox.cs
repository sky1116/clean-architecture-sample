using Competition.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Competition.Infrastructure.Persistence.Migrations;

/// <summary>Adds durable integration events without modifying existing competition records.</summary>
[DbContext(typeof(CompetitionDbContext))]
[Migration("20261020140200_DurableOutbox")]
public sealed class DurableOutbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PendingEvents",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CompetitionId = table.Column<Guid>(type: "uuid", nullable: false),
                EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Payload = table.Column<string>(type: "jsonb", nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                AttemptCount = table.Column<int>(type: "integer", nullable: false),
                NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                DeadLetteredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastFailureCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_PendingEvents", x => x.Id));
        migrationBuilder.CreateIndex("IX_PendingEvents_PublishedAt_DeadLetteredAt_NextAttemptAt",
            "PendingEvents", new[] { "PublishedAt", "DeadLetteredAt", "NextAttemptAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable("PendingEvents");
}
