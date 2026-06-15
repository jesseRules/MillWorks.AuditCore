using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MillWorks.AuditCore.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditLogEnvelopeIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "EnvelopeId",
                schema: "audit",
                table: "AuditLogs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Envelope_Property",
                schema: "audit",
                table: "AuditLogs",
                columns: new[] { "EnvelopeId", "PropertyName" },
                unique: true,
                filter: "[EnvelopeId] IS NOT NULL AND [PropertyName] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_Envelope_Property",
                schema: "audit",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "EnvelopeId",
                schema: "audit",
                table: "AuditLogs");
        }
    }
}
