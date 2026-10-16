using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MillWorks.AuditCore.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxOwnershipEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "FailureCycleId",
                schema: "audit",
                table: "AuditOutbox",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FailureTenantId",
                schema: "audit",
                table: "AuditOutbox",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ResolutionId",
                schema: "audit",
                table: "AuditOutbox",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AuditOutboxFailureHandoffs",
                schema: "audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OutboxRowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FailureCycleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EnvelopeJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NextRetryAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClaimToken = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AttemptCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditOutboxFailureHandoffs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditOutboxOperations",
                schema: "audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    VerifyUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditOutboxOperations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditOutboxResolutions",
                schema: "audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OutboxRowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FailureCycleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditOutboxResolutions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditOutboxFailureHandoffs_OutboxRowId_FailureCycleId",
                schema: "audit",
                table: "AuditOutboxFailureHandoffs",
                columns: new[] { "OutboxRowId", "FailureCycleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditOutboxFailureHandoffs_Status_NextRetryAt_LeaseExpiresAt_CreatedAt",
                schema: "audit",
                table: "AuditOutboxFailureHandoffs",
                columns: new[] { "Status", "NextRetryAt", "LeaseExpiresAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditOutboxOperations_VerifyUntil",
                schema: "audit",
                table: "AuditOutboxOperations",
                column: "VerifyUntil");

            migrationBuilder.CreateIndex(
                name: "IX_AuditOutboxResolutions_OutboxRowId_FailureCycleId",
                schema: "audit",
                table: "AuditOutboxResolutions",
                columns: new[] { "OutboxRowId", "FailureCycleId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditOutboxFailureHandoffs",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "AuditOutboxOperations",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "AuditOutboxResolutions",
                schema: "audit");

            migrationBuilder.DropColumn(
                name: "FailureCycleId",
                schema: "audit",
                table: "AuditOutbox");

            migrationBuilder.DropColumn(
                name: "FailureTenantId",
                schema: "audit",
                table: "AuditOutbox");

            migrationBuilder.DropColumn(
                name: "ResolutionId",
                schema: "audit",
                table: "AuditOutbox");
        }
    }
}
