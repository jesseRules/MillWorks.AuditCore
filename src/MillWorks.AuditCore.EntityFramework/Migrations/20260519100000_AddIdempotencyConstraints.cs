using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MillWorks.AuditCore.EntityFramework.Migrations;

/// <inheritdoc />
public partial class AddIdempotencyConstraints : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Add IdempotencyKey column to AuditOutbox.
        // For ExplicitEvent: IdempotencyKey = AuditEnvelope.EnvelopeId (which tracks the explicit event)
        // For EntityChange: IdempotencyKey = AuditEnvelope.EnvelopeId (stable envelope identity)
        //
        // Added as nullable first, then backfilled, then made NOT NULL to avoid
        // unique constraint violations on existing rows with default Guid.Empty.
        migrationBuilder.AddColumn<Guid>(
            name: "IdempotencyKey",
            schema: "audit",
            table: "AuditOutbox",
            type: "uniqueidentifier",
            nullable: true);

        // Backfill existing rows with their Id (guaranteed unique).
        // This is a greenfield database, so this should be a no-op in production,
        // but handles dev/test databases with existing outbox rows.
        migrationBuilder.Sql(
            "UPDATE [audit].[AuditOutbox] SET [IdempotencyKey] = [Id] WHERE [IdempotencyKey] IS NULL");

        // Now make the column NOT NULL.
        migrationBuilder.AlterColumn<Guid>(
            name: "IdempotencyKey",
            schema: "audit",
            table: "AuditOutbox",
            type: "uniqueidentifier",
            nullable: false,
            oldClrType: typeof(Guid),
            oldNullable: true);

        // Unique index on AuditOutbox.IdempotencyKey for duplicate envelope detection.
        // Prevents the same envelope from being queued twice, even across retries.
        migrationBuilder.CreateIndex(
            name: "UX_AuditOutbox_IdempotencyKey",
            schema: "audit",
            table: "AuditOutbox",
            column: "IdempotencyKey",
            unique: true);

        // Note: AuditEvents.EventId is the primary key, so it's already unique.
        // Duplicate explicit event detection relies on the PK constraint and is
        // handled by catching DbUpdateException in the batch writers.
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "UX_AuditOutbox_IdempotencyKey",
            schema: "audit",
            table: "AuditOutbox");

        migrationBuilder.DropColumn(
            name: "IdempotencyKey",
            schema: "audit",
            table: "AuditOutbox");
    }
}
