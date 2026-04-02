using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MillWorks.AuditCore.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class PhaseThreeStartupParity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<byte[]>(
                name: "RowVersion",
                schema: "audit",
                table: "SecurityEvents",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                oldClrType: typeof(byte[]),
                oldType: "varbinary(max)");

            migrationBuilder.AlterColumn<byte[]>(
                name: "RowVersion",
                schema: "audit",
                table: "AuditEvents",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                oldClrType: typeof(byte[]),
                oldType: "varbinary(max)");

            migrationBuilder.AddColumn<int>(
                name: "IntegrityStatus",
                schema: "audit",
                table: "AuditEvents",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AlterColumn<byte[]>(
                name: "RowVersion",
                schema: "audit",
                table: "ArchiveRecord",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                oldClrType: typeof(byte[]),
                oldType: "varbinary(max)");

            migrationBuilder.CreateTable(
                name: "AuditIntegrityWorkItems",
                schema: "audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    LeaseOwner = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditIntegrityWorkItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditIntegrityWorkItems_AuditEvents_EventId",
                        column: x => x.EventId,
                        principalSchema: "audit",
                        principalTable: "AuditEvents",
                        principalColumn: "EventId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_IntegrityStatus",
                schema: "audit",
                table: "AuditEvents",
                column: "IntegrityStatus");

            migrationBuilder.CreateIndex(
                name: "IX_IntegrityWorkItems_EventId",
                schema: "audit",
                table: "AuditIntegrityWorkItems",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntegrityWorkItems_LeaseExpiry",
                schema: "audit",
                table: "AuditIntegrityWorkItems",
                column: "LeaseExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_IntegrityWorkItems_Status",
                schema: "audit",
                table: "AuditIntegrityWorkItems",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_IntegrityWorkItems_Status_CreatedAt",
                schema: "audit",
                table: "AuditIntegrityWorkItems",
                columns: new[] { "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditIntegrityWorkItems",
                schema: "audit");

            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_IntegrityStatus",
                schema: "audit",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "IntegrityStatus",
                schema: "audit",
                table: "AuditEvents");

            migrationBuilder.AlterColumn<byte[]>(
                name: "RowVersion",
                schema: "audit",
                table: "SecurityEvents",
                type: "varbinary(max)",
                nullable: false,
                oldClrType: typeof(byte[]),
                oldType: "rowversion",
                oldRowVersion: true);

            migrationBuilder.AlterColumn<byte[]>(
                name: "RowVersion",
                schema: "audit",
                table: "AuditEvents",
                type: "varbinary(max)",
                nullable: false,
                oldClrType: typeof(byte[]),
                oldType: "rowversion",
                oldRowVersion: true);

            migrationBuilder.AlterColumn<byte[]>(
                name: "RowVersion",
                schema: "audit",
                table: "ArchiveRecord",
                type: "varbinary(max)",
                nullable: false,
                oldClrType: typeof(byte[]),
                oldType: "rowversion",
                oldRowVersion: true);
        }
    }
}
