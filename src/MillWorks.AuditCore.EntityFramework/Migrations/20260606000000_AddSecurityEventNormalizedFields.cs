using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MillWorks.AuditCore.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class AddSecurityEventNormalizedFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                schema: "audit",
                table: "SecurityEvents",
                type: "nvarchar(36)",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "audit",
                table: "SecurityEvents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ActorUserId",
                schema: "audit",
                table: "SecurityEvents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SubjectUserId",
                schema: "audit",
                table: "SecurityEvents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceIpHash",
                schema: "audit",
                table: "SecurityEvents",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserAgentHash",
                schema: "audit",
                table: "SecurityEvents",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Operation",
                schema: "audit",
                table: "SecurityEvents",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvents_TenantId",
                schema: "audit",
                table: "SecurityEvents",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvents_ActorUserId",
                schema: "audit",
                table: "SecurityEvents",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvents_SubjectUserId",
                schema: "audit",
                table: "SecurityEvents",
                column: "SubjectUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvents_CorrelationId",
                schema: "audit",
                table: "SecurityEvents",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvents_Operation",
                schema: "audit",
                table: "SecurityEvents",
                column: "Operation");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SecurityEvents_Operation",
                schema: "audit",
                table: "SecurityEvents");

            migrationBuilder.DropIndex(
                name: "IX_SecurityEvents_CorrelationId",
                schema: "audit",
                table: "SecurityEvents");

            migrationBuilder.DropIndex(
                name: "IX_SecurityEvents_SubjectUserId",
                schema: "audit",
                table: "SecurityEvents");

            migrationBuilder.DropIndex(
                name: "IX_SecurityEvents_ActorUserId",
                schema: "audit",
                table: "SecurityEvents");

            migrationBuilder.DropIndex(
                name: "IX_SecurityEvents_TenantId",
                schema: "audit",
                table: "SecurityEvents");

            migrationBuilder.DropColumn(
                name: "Operation",
                schema: "audit",
                table: "SecurityEvents");

            migrationBuilder.DropColumn(
                name: "UserAgentHash",
                schema: "audit",
                table: "SecurityEvents");

            migrationBuilder.DropColumn(
                name: "SourceIpHash",
                schema: "audit",
                table: "SecurityEvents");

            migrationBuilder.DropColumn(
                name: "SubjectUserId",
                schema: "audit",
                table: "SecurityEvents");

            migrationBuilder.DropColumn(
                name: "ActorUserId",
                schema: "audit",
                table: "SecurityEvents");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "audit",
                table: "SecurityEvents");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                schema: "audit",
                table: "SecurityEvents");
        }
    }
}
