using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MillWorks.AuditCore.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxNextRetryAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextRetryAt",
                schema: "audit",
                table: "AuditOutbox",
                type: "datetimeoffset",
                nullable: true);

            // Composite index for efficient drainer polling:
            // WHERE Status = Pending AND (NextRetryAt IS NULL OR NextRetryAt <= @now)
            // ORDER BY CreatedAt
            migrationBuilder.CreateIndex(
                name: "IX_AuditOutbox_Status_NextRetryAt_CreatedAt",
                schema: "audit",
                table: "AuditOutbox",
                columns: new[] { "Status", "NextRetryAt", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditOutbox_Status_NextRetryAt_CreatedAt",
                schema: "audit",
                table: "AuditOutbox");

            migrationBuilder.DropColumn(
                name: "NextRetryAt",
                schema: "audit",
                table: "AuditOutbox");
        }
    }
}
