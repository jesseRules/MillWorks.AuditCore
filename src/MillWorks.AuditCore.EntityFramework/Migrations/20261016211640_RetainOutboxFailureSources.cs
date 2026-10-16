using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MillWorks.AuditCore.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class RetainOutboxFailureSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "FK_AuditOutboxFailureHandoffs_AuditOutbox_OutboxRowId",
                schema: "audit",
                table: "AuditOutboxFailureHandoffs",
                column: "OutboxRowId",
                principalSchema: "audit",
                principalTable: "AuditOutbox",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AuditOutboxFailureHandoffs_AuditOutbox_OutboxRowId",
                schema: "audit",
                table: "AuditOutboxFailureHandoffs");
        }
    }
}
