using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MillWorks.AuditCore.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class SecurityEventAppendOnlyCleanup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Resolution",
                schema: "audit",
                table: "SecurityEvents");

            migrationBuilder.DropColumn(
                name: "ResolvedAt",
                schema: "audit",
                table: "SecurityEvents");

            migrationBuilder.DropColumn(
                name: "ResolvedBy",
                schema: "audit",
                table: "SecurityEvents");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Resolution",
                schema: "audit",
                table: "SecurityEvents",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ResolvedAt",
                schema: "audit",
                table: "SecurityEvents",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolvedBy",
                schema: "audit",
                table: "SecurityEvents",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);
        }
    }
}
