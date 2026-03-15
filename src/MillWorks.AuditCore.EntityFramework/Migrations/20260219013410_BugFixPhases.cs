using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MillWorks.AuditCore.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class BugFixPhases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_EventId",
                schema: "audit",
                table: "AuditEvents");

            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                schema: "audit",
                table: "AuditLogs",
                type: "nvarchar(36)",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Checksum",
                schema: "audit",
                table: "AuditIntegrity",
                type: "char(44)",
                maxLength: 44,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "char(32)",
                oldMaxLength: 32);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_CorrelationId",
                schema: "audit",
                table: "AuditLogs",
                column: "CorrelationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_CorrelationId",
                schema: "audit",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                schema: "audit",
                table: "AuditLogs");

            migrationBuilder.AlterColumn<string>(
                name: "Checksum",
                schema: "audit",
                table: "AuditIntegrity",
                type: "char(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "char(44)",
                oldMaxLength: 44);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_EventId",
                schema: "audit",
                table: "AuditEvents",
                column: "EventId",
                unique: true);
        }
    }
}
