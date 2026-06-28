using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MillWorks.AuditCore.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class SecurityEventStatusRemoval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SecurityEvents_Status",
                schema: "audit",
                table: "SecurityEvents");

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "audit",
                table: "SecurityEvents");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Status",
                schema: "audit",
                table: "SecurityEvents",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvents_Status",
                schema: "audit",
                table: "SecurityEvents",
                column: "Status");
        }
    }
}
