using Competition.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Competition.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CompetitionDbContext))]
[Migration("20261020140400_OperatorRedriveAudit")]
public sealed class OperatorRedriveAudit : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.CreateTable(
            name: "OutboxRecoveryAudits",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                EventId = table.Column<Guid>(type: "uuid", nullable: false),
                AdministratorId = table.Column<Guid>(type: "uuid", nullable: false),
                Reason = table.Column<string>(type: "character varying(300)",
                    maxLength: 300, nullable: false),
                PreviousAttempts = table.Column<int>(type: "integer", nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(
                    type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_OutboxRecoveryAudits", x => x.Id));
        builder.CreateIndex(
            name: "IX_OutboxRecoveryAudits_EventId_OccurredAt",
            table: "OutboxRecoveryAudits",
            columns: new[] { "EventId", "OccurredAt" });
    }

    protected override void Down(MigrationBuilder builder) =>
        builder.DropTable("OutboxRecoveryAudits");
}
