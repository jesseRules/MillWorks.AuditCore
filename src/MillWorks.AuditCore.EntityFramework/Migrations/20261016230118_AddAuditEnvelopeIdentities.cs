using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MillWorks.AuditCore.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditEnvelopeIdentities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditEnvelopeIdentities",
                schema: "audit",
                columns: table => new
                {
                    Channel = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    LogicalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContentVersion = table.Column<int>(type: "int", nullable: false),
                    Fingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEnvelopeIdentities", x => new { x.Channel, x.LogicalId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEnvelopeIdentities_TenantId",
                schema: "audit",
                table: "AuditEnvelopeIdentities",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEnvelopeIdentities",
                schema: "audit");
        }
    }
}
