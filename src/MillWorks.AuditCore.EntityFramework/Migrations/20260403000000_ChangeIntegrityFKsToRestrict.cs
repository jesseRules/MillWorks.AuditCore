using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MillWorks.AuditCore.EntityFramework.Migrations
{
    /// <summary>
    /// Changes CASCADE DELETE to RESTRICT on AuditIntegrity and AuditIntegrityWorkItems
    /// foreign keys to AuditEvents. CASCADE DELETE on tamper-evidence tables would allow
    /// a hard-delete of an AuditEvent to silently destroy its integrity proof records,
    /// undermining the entire audit chain.
    /// </summary>
    public partial class ChangeIntegrityFKsToRestrict : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AuditIntegrity_AuditEvents_EventId",
                schema: "audit",
                table: "AuditIntegrity");

            migrationBuilder.DropForeignKey(
                name: "FK_AuditIntegrityWorkItems_AuditEvents_EventId",
                schema: "audit",
                table: "AuditIntegrityWorkItems");

            migrationBuilder.AddForeignKey(
                name: "FK_AuditIntegrity_AuditEvents_EventId",
                schema: "audit",
                table: "AuditIntegrity",
                column: "EventId",
                principalSchema: "audit",
                principalTable: "AuditEvents",
                principalColumn: "EventId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AuditIntegrityWorkItems_AuditEvents_EventId",
                schema: "audit",
                table: "AuditIntegrityWorkItems",
                column: "EventId",
                principalSchema: "audit",
                principalTable: "AuditEvents",
                principalColumn: "EventId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AuditIntegrity_AuditEvents_EventId",
                schema: "audit",
                table: "AuditIntegrity");

            migrationBuilder.DropForeignKey(
                name: "FK_AuditIntegrityWorkItems_AuditEvents_EventId",
                schema: "audit",
                table: "AuditIntegrityWorkItems");

            migrationBuilder.AddForeignKey(
                name: "FK_AuditIntegrity_AuditEvents_EventId",
                schema: "audit",
                table: "AuditIntegrity",
                column: "EventId",
                principalSchema: "audit",
                principalTable: "AuditEvents",
                principalColumn: "EventId",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AuditIntegrityWorkItems_AuditEvents_EventId",
                schema: "audit",
                table: "AuditIntegrityWorkItems",
                column: "EventId",
                principalSchema: "audit",
                principalTable: "AuditEvents",
                principalColumn: "EventId",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
