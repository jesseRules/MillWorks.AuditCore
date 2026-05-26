using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MillWorks.AuditCore.EntityFramework.Migrations
{
    /// <summary>
    /// Adds idempotency and lease columns to AuditOutbox for Slices C and D of the Batch Publishing Redesign.
    /// </summary>
    public partial class OutboxIdempotencyAndLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Slice C: Add IdempotencyKey column
            migrationBuilder.AddColumn<Guid>(
                name: "IdempotencyKey",
                schema: "audit",
                table: "AuditOutbox",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: Guid.Empty);

            // Slice D: Add lease columns
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseExpiresAt",
                schema: "audit",
                table: "AuditOutbox",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeaseOwner",
                schema: "audit",
                table: "AuditOutbox",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            // Drop old index before creating new covering index
            migrationBuilder.DropIndex(
                name: "IX_AuditOutbox_Status_NextRetryAt_CreatedAt",
                schema: "audit",
                table: "AuditOutbox");

            // Slice C: Create unique index on IdempotencyKey
            migrationBuilder.CreateIndex(
                name: "UX_AuditOutbox_IdempotencyKey",
                schema: "audit",
                table: "AuditOutbox",
                column: "IdempotencyKey",
                unique: true);

            // Slice D: Create covering index for claim queries
            migrationBuilder.CreateIndex(
                name: "IX_AuditOutbox_Claimable",
                schema: "audit",
                table: "AuditOutbox",
                columns: new[] { "Status", "NextRetryAt", "LeaseExpiresAt", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditOutbox_Claimable",
                schema: "audit",
                table: "AuditOutbox");

            migrationBuilder.DropIndex(
                name: "UX_AuditOutbox_IdempotencyKey",
                schema: "audit",
                table: "AuditOutbox");

            migrationBuilder.CreateIndex(
                name: "IX_AuditOutbox_Status_NextRetryAt_CreatedAt",
                schema: "audit",
                table: "AuditOutbox",
                columns: new[] { "Status", "NextRetryAt", "CreatedAt" });

            migrationBuilder.DropColumn(
                name: "LeaseOwner",
                schema: "audit",
                table: "AuditOutbox");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAt",
                schema: "audit",
                table: "AuditOutbox");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                schema: "audit",
                table: "AuditOutbox");
        }
    }
}
