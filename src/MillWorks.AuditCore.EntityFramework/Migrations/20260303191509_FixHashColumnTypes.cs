using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MillWorks.AuditCore.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class FixHashColumnTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "PreviousEventHash",
                schema: "audit",
                table: "AuditIntegrity",
                type: "varchar(44)",
                maxLength: 44,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "char(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "HmacSignature",
                schema: "audit",
                table: "AuditIntegrity",
                type: "varchar(44)",
                maxLength: 44,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "char(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "EventHash",
                schema: "audit",
                table: "AuditIntegrity",
                type: "varchar(44)",
                maxLength: 44,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "char(64)",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<string>(
                name: "Checksum",
                schema: "audit",
                table: "AuditIntegrity",
                type: "varchar(44)",
                maxLength: 44,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "char(44)",
                oldMaxLength: 44);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "PreviousEventHash",
                schema: "audit",
                table: "AuditIntegrity",
                type: "char(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(44)",
                oldMaxLength: 44,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "HmacSignature",
                schema: "audit",
                table: "AuditIntegrity",
                type: "char(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(44)",
                oldMaxLength: 44,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "EventHash",
                schema: "audit",
                table: "AuditIntegrity",
                type: "char(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(44)",
                oldMaxLength: 44);

            migrationBuilder.AlterColumn<string>(
                name: "Checksum",
                schema: "audit",
                table: "AuditIntegrity",
                type: "char(44)",
                maxLength: 44,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(44)",
                oldMaxLength: 44);
        }
    }
}
