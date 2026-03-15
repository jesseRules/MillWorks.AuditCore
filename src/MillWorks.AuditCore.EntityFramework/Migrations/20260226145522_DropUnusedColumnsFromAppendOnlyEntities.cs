using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MillWorks.AuditCore.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class DropUnusedColumnsFromAppendOnlyEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "audit",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "DeletedById",
                schema: "audit",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                schema: "audit",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "audit",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                schema: "audit",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "UpdatedById",
                schema: "audit",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "audit",
                table: "AuditIntegrity");

            migrationBuilder.DropColumn(
                name: "DeletedById",
                schema: "audit",
                table: "AuditIntegrity");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                schema: "audit",
                table: "AuditIntegrity");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "audit",
                table: "AuditIntegrity");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                schema: "audit",
                table: "AuditIntegrity");

            migrationBuilder.DropColumn(
                name: "UpdatedById",
                schema: "audit",
                table: "AuditIntegrity");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                schema: "audit",
                table: "AuditLogs",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeletedById",
                schema: "audit",
                table: "AuditLogs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                schema: "audit",
                table: "AuditLogs",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                schema: "audit",
                table: "AuditLogs",
                type: "varbinary(max)",
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                schema: "audit",
                table: "AuditLogs",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UpdatedById",
                schema: "audit",
                table: "AuditLogs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                schema: "audit",
                table: "AuditIntegrity",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeletedById",
                schema: "audit",
                table: "AuditIntegrity",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                schema: "audit",
                table: "AuditIntegrity",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                schema: "audit",
                table: "AuditIntegrity",
                type: "varbinary(max)",
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                schema: "audit",
                table: "AuditIntegrity",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UpdatedById",
                schema: "audit",
                table: "AuditIntegrity",
                type: "uniqueidentifier",
                nullable: true);
        }
    }
}
