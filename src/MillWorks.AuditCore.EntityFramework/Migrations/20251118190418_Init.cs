using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MillWorks.AuditCore.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class Init : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "audit");

            migrationBuilder.CreateTable(
                name: "ArchiveRecord",
                schema: "audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArchiveId = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    BlobName = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    ContainerName = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    EventCount = table.Column<int>(type: "int", nullable: false),
                    DateRangeStart = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DateRangeEnd = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Hash = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    ArchiveVersion = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false, defaultValue: "1.0"),
                    CompressionType = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false, defaultValue: "gzip"),
                    ErrorMessage = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Metadata = table.Column<string>(type: "nvarchar(max)", maxLength: 4000, nullable: true),
                    LastVerifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "varchar(450)", maxLength: 450, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchiveRecord", x => x.Id);
                    table.CheckConstraint("CK_ArchiveRecord_DateRange", "[DateRangeEnd] >= [DateRangeStart]");
                });

            migrationBuilder.CreateTable(
                name: "AuditEvents",
                schema: "audit",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    InsertedDate = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true, defaultValueSql: "GETUTCDATE()"),
                    LastUpdatedDate = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    JsonData = table.Column<string>(type: "nvarchar(max)", maxLength: 4000, nullable: true),
                    EventType = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    User = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    UserEnvName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    AppUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AspNetUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    UserFullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    StartDate = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EndDate = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Duration = table.Column<int>(type: "int", nullable: true),
                    EntityType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Action = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    AdditionalData = table.Column<string>(type: "nvarchar(max)", maxLength: 4000, nullable: true),
                    Environment = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true, defaultValue: "Production"),
                    MachineName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CallingMethodName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AssemblyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RequestPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RequestMethod = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                schema: "audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Action = table.Column<int>(type: "int", nullable: false),
                    PropertyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    OldValue = table.Column<string>(type: "nvarchar(max)", maxLength: 4000, nullable: true),
                    NewValue = table.Column<string>(type: "nvarchar(max)", maxLength: 4000, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AdditionalData = table.Column<string>(type: "nvarchar(max)", maxLength: 4000, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IpAddress = table.Column<string>(type: "varchar(45)", maxLength: 45, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                    table.CheckConstraint("CK_AuditLogs_Action", "[Action] >= 0 AND [Action] <= 10");
                });

            migrationBuilder.CreateTable(
                name: "AuditIntegrity",
                schema: "audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventHash = table.Column<string>(type: "char(64)", maxLength: 64, nullable: false),
                    PreviousEventHash = table.Column<string>(type: "char(64)", maxLength: 64, nullable: true),
                    DigitalSignature = table.Column<string>(type: "varchar(512)", maxLength: 512, nullable: true),
                    TrustedTimestamp = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SequenceNumber = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    HmacSignature = table.Column<string>(type: "char(64)", maxLength: 64, nullable: true),
                    Checksum = table.Column<string>(type: "char(32)", maxLength: 32, nullable: false),
                    AlgorithmVersion = table.Column<int>(type: "int", nullable: false),
                    Parameters = table.Column<string>(type: "nvarchar(max)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditIntegrity", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditIntegrity_AuditEvents_EventId",
                        column: x => x.EventId,
                        principalSchema: "audit",
                        principalTable: "AuditEvents",
                        principalColumn: "EventId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SecurityEvents",
                schema: "audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<int>(type: "int", nullable: false),
                    Severity = table.Column<int>(type: "int", nullable: false),
                    RelatedAuditEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Message = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Details = table.Column<string>(type: "nvarchar(max)", maxLength: 4000, nullable: true),
                    DetectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DetectedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResolvedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Resolution = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SecurityEvents_AuditEvents_RelatedAuditEventId",
                        column: x => x.RelatedAuditEventId,
                        principalSchema: "audit",
                        principalTable: "AuditEvents",
                        principalColumn: "EventId");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Archive_Record_ArchiveId",
                schema: "audit",
                table: "ArchiveRecord",
                column: "ArchiveId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Archive_Record_ContainerName",
                schema: "audit",
                table: "ArchiveRecord",
                column: "ContainerName");

            migrationBuilder.CreateIndex(
                name: "IX_Archive_Record_CreatedAt",
                schema: "audit",
                table: "ArchiveRecord",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Archive_Record_CreatedByUserId",
                schema: "audit",
                table: "ArchiveRecord",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Archive_Record_DateRange",
                schema: "audit",
                table: "ArchiveRecord",
                columns: new[] { "DateRangeStart", "DateRangeEnd" });

            migrationBuilder.CreateIndex(
                name: "IX_Archive_Record_LastVerifiedAt",
                schema: "audit",
                table: "ArchiveRecord",
                column: "LastVerifiedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Archive_Record_Status",
                schema: "audit",
                table: "ArchiveRecord",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_AspNetUserId",
                schema: "audit",
                table: "AuditEvents",
                column: "AspNetUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_CorrelationId",
                schema: "audit",
                table: "AuditEvents",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_Date_Type",
                schema: "audit",
                table: "AuditEvents",
                columns: new[] { "InsertedDate", "EventType" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_Entity",
                schema: "audit",
                table: "AuditEvents",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_EventId",
                schema: "audit",
                table: "AuditEvents",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_EventType",
                schema: "audit",
                table: "AuditEvents",
                column: "EventType");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_EventType_Date_Filtered",
                schema: "audit",
                table: "AuditEvents",
                columns: new[] { "EventType", "InsertedDate" },
                filter: "[EventType] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_TenantId",
                schema: "audit",
                table: "AuditEvents",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_UserId",
                schema: "audit",
                table: "AuditEvents",
                column: "AppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditIntegrity_EventId",
                schema: "audit",
                table: "AuditIntegrity",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditIntegrity_HashChain",
                schema: "audit",
                table: "AuditIntegrity",
                columns: new[] { "EventHash", "PreviousEventHash" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditIntegrity_SequenceNumber",
                schema: "audit",
                table: "AuditIntegrity",
                column: "SequenceNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditIntegrity_Timestamp",
                schema: "audit",
                table: "AuditIntegrity",
                column: "TrustedTimestamp");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Action",
                schema: "audit",
                table: "AuditLogs",
                column: "Action");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_CreatedAt",
                schema: "audit",
                table: "AuditLogs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_CreatedBy",
                schema: "audit",
                table: "AuditLogs",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Date_Entity",
                schema: "audit",
                table: "AuditLogs",
                columns: new[] { "CreatedAt", "EntityType" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Entity",
                schema: "audit",
                table: "AuditLogs",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvents_DetectedAt",
                schema: "audit",
                table: "SecurityEvents",
                column: "DetectedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvents_EventType",
                schema: "audit",
                table: "SecurityEvents",
                column: "EventType");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvents_RelatedAuditEventId",
                schema: "audit",
                table: "SecurityEvents",
                column: "RelatedAuditEventId");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvents_Severity",
                schema: "audit",
                table: "SecurityEvents",
                column: "Severity");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvents_Status",
                schema: "audit",
                table: "SecurityEvents",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArchiveRecord",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "AuditIntegrity",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "AuditLogs",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "SecurityEvents",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "AuditEvents",
                schema: "audit");
        }
    }
}
