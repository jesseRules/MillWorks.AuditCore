using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MillWorks.AuditCore.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class AddIntegritySigningKeyIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DigitalSignatureKeyId",
                schema: "audit",
                table: "AuditIntegrity",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HmacKeyId",
                schema: "audit",
                table: "AuditIntegrity",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DigitalSignatureKeyId",
                schema: "audit",
                table: "AuditIntegrity");

            migrationBuilder.DropColumn(
                name: "HmacKeyId",
                schema: "audit",
                table: "AuditIntegrity");
        }
    }
}
