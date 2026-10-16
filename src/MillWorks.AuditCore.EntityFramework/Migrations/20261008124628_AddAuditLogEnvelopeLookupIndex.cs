using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MillWorks.AuditCore.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditLogEnvelopeLookupIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Envelope_Lookup",
                schema: "audit",
                table: "AuditLogs",
                column: "EnvelopeId",
                filter: "[EnvelopeId] IS NOT NULL")
                .Annotation("SqlServer:Include", new[] { "PropertyName" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_Envelope_Lookup",
                schema: "audit",
                table: "AuditLogs");
        }
    }
}
