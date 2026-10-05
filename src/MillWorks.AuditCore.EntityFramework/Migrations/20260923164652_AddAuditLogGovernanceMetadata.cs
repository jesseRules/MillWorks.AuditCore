using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MillWorks.AuditCore.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditLogGovernanceMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GovernanceMetadata",
                schema: "audit",
                table: "AuditLogs",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GovernanceMetadata",
                schema: "audit",
                table: "AuditLogs");
        }
    }
}
