using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MillWorks.AuditCore.EntityFramework.Migrations;

/// <inheritdoc />
public partial class AddLeaseColumns : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var isSqlServer = migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer";
        var tableName = isSqlServer ? "[audit].[AuditOutbox]" : "AuditOutbox";

        // Update AuditOutboxStatus enum: shift Failed from 2 to 3 to make room for InFlight = 1.
        // Must do this before adding the new InFlight status to existing rows.
        // Current values: Pending=0, Completed=1, Failed=2
        // New values:     Pending=0, InFlight=1, Completed=2, Failed=3
        //
        // Step 1: Move Completed (1) → temp value (99) to avoid collision
        // Step 2: Move Failed (2) → new value (3)
        // Step 3: Move Completed back (99) → new value (2)
        migrationBuilder.Sql($"UPDATE {tableName} SET Status = 99 WHERE Status = 1");
        migrationBuilder.Sql($"UPDATE {tableName} SET Status = 3 WHERE Status = 2");
        migrationBuilder.Sql($"UPDATE {tableName} SET Status = 2 WHERE Status = 99");

        // Add lease columns for row-level claim ownership.
        // LeaseOwner: unique identifier for the drainer instance that claimed the row.
        // LeaseExpiresAt: when the lease expires (allows other drainers to reclaim on crash).
        migrationBuilder.AddColumn<string>(
            name: "LeaseOwner",
            schema: isSqlServer ? "audit" : null,
            table: "AuditOutbox",
            type: isSqlServer ? "nvarchar(100)" : "TEXT",
            maxLength: 100,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "LeaseExpiresAt",
            schema: isSqlServer ? "audit" : null,
            table: "AuditOutbox",
            type: isSqlServer ? "datetimeoffset" : "TEXT",
            nullable: true);

        // Drop the old status index and create a covering index optimized for the claim query:
        // WHERE Status=Pending AND (NextRetryAt IS NULL OR NextRetryAt <= @now)
        //       AND (LeaseExpiresAt IS NULL OR LeaseExpiresAt < @now)
        // ORDER BY CreatedAt
        migrationBuilder.DropIndex(
            name: "IX_AuditOutbox_Status",
            schema: isSqlServer ? "audit" : null,
            table: "AuditOutbox");

        // SQL Server: Use INCLUDE columns to avoid key lookups when reading claimed rows.
        // SQLite/others: Use standard composite index (INCLUDE not supported).
        if (isSqlServer)
        {
            migrationBuilder.Sql(@"
                CREATE INDEX [IX_AuditOutbox_Claimable]
                ON [audit].[AuditOutbox] ([Status], [NextRetryAt], [LeaseExpiresAt], [CreatedAt])
                INCLUDE ([EnvelopeJson], [EnvelopeVersion], [IdempotencyKey], [AttemptCount], [LastError], [CompletedAt])");
        }
        else
        {
            migrationBuilder.CreateIndex(
                name: "IX_AuditOutbox_Claimable",
                table: "AuditOutbox",
                columns: new[] { "Status", "NextRetryAt", "LeaseExpiresAt", "CreatedAt" });
        }
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        var isSqlServer = migrationBuilder.ActiveProvider == "Microsoft.EntityFrameworkCore.SqlServer";
        var tableName = isSqlServer ? "[audit].[AuditOutbox]" : "AuditOutbox";

        if (isSqlServer)
        {
            migrationBuilder.Sql("DROP INDEX [IX_AuditOutbox_Claimable] ON [audit].[AuditOutbox]");
        }
        else
        {
            migrationBuilder.DropIndex(name: "IX_AuditOutbox_Claimable", table: "AuditOutbox");
        }

        migrationBuilder.CreateIndex(
            name: "IX_AuditOutbox_Status",
            schema: isSqlServer ? "audit" : null,
            table: "AuditOutbox",
            column: "Status");

        migrationBuilder.DropColumn(
            name: "LeaseExpiresAt",
            schema: isSqlServer ? "audit" : null,
            table: "AuditOutbox");

        migrationBuilder.DropColumn(
            name: "LeaseOwner",
            schema: isSqlServer ? "audit" : null,
            table: "AuditOutbox");

        // Revert enum values: Failed (3) → (2), Completed (2) → (1)
        // Step 1: Move Completed (2) → temp (99)
        // Step 2: Move Failed (3) → old value (2)
        // Step 3: Move Completed back (99) → old value (1)
        migrationBuilder.Sql($"UPDATE {tableName} SET Status = 99 WHERE Status = 2");
        migrationBuilder.Sql($"UPDATE {tableName} SET Status = 2 WHERE Status = 3");
        migrationBuilder.Sql($"UPDATE {tableName} SET Status = 1 WHERE Status = 99");

        // Any InFlight (1) rows need to revert to Pending (0) since InFlight doesn't exist in old enum
        migrationBuilder.Sql($"UPDATE {tableName} SET Status = 0 WHERE Status = 1");
    }
}
