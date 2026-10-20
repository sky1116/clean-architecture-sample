using Competition.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Competition.Infrastructure.Persistence.Migrations;

/// <summary>Consumer-side durable event receipts and independent projected score state.</summary>
[DbContext(typeof(CompetitionDbContext))]
[Migration("20261020140300_InboxProjection")]
public sealed class InboxProjection : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.CreateTable(
            name: "InboxReceipts",
            columns: table => new
            {
                EventId = table.Column<Guid>(type: "uuid", nullable: false),
                CompetitionId = table.Column<Guid>(type: "uuid", nullable: false),
                ResultId = table.Column<Guid>(type: "uuid", nullable: false),
                Revision = table.Column<int>(type: "integer", nullable: false),
                EventType = table.Column<string>(type: "character varying(100)",
                    maxLength: 100, nullable: false),
                PayloadDigest = table.Column<string>(type: "character varying(64)",
                    maxLength: 64, nullable: false),
                ReceivedAt = table.Column<DateTimeOffset>(
                    type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_InboxReceipts", x => x.EventId));

        builder.CreateTable(
            name: "ReceivedResults",
            columns: table => new
            {
                CompetitionId = table.Column<Guid>(type: "uuid", nullable: false),
                ResultId = table.Column<Guid>(type: "uuid", nullable: false),
                ParticipantId = table.Column<Guid>(type: "uuid", nullable: false),
                EffectivePoints = table.Column<int>(type: "integer", nullable: false),
                Revision = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_ReceivedResults",
                x => new { x.CompetitionId, x.ResultId }));

        builder.CreateIndex(
            name: "IX_InboxReceipts_CompetitionId_ResultId_Revision",
            table: "InboxReceipts",
            columns: new[] { "CompetitionId", "ResultId", "Revision" },
            unique: true);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.DropTable("InboxReceipts");
        builder.DropTable("ReceivedResults");
    }
}
