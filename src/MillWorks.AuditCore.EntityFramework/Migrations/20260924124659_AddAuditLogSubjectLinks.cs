using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MillWorks.AuditCore.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditLogSubjectLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditLogSubjectLinks",
                schema: "audit",
                columns: table => new
                {
                    AuditLogId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Relationship = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogSubjectLinks", x => new { x.AuditLogId, x.TenantId, x.SubjectId, x.Relationship });
                    table.ForeignKey(
                        name: "FK_AuditLogSubjectLinks_AuditLogs_AuditLogId",
                        column: x => x.AuditLogId,
                        principalSchema: "audit",
                        principalTable: "AuditLogs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogSubjectLinks_Subject_Time",
                schema: "audit",
                table: "AuditLogSubjectLinks",
                columns: new[] { "TenantId", "SubjectId", "OccurredAt", "AuditLogId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLogSubjectLinks",
                schema: "audit");
        }
    }
}
