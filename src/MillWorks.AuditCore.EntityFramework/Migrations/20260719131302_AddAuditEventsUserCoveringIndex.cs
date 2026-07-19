using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MillWorks.AuditCore.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditEventsUserCoveringIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_Date_Type",
                schema: "audit",
                table: "AuditEvents");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_Date_Type",
                schema: "audit",
                table: "AuditEvents",
                columns: new[] { "InsertedDate", "EventType" })
                .Annotation("SqlServer:Include", new[] { "User" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_Date_Type",
                schema: "audit",
                table: "AuditEvents");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_Date_Type",
                schema: "audit",
                table: "AuditEvents",
                columns: new[] { "InsertedDate", "EventType" });
        }
    }
}
