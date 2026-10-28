using Microsoft.EntityFrameworkCore.Migrations;
using MillWorks.AuditCore.EntityFramework.Data;

#nullable disable

namespace MillWorks.AuditCore.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditEventWordSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider != "Microsoft.EntityFrameworkCore.SqlServer") return;
            using var stream = typeof(AuditDbContext).Assembly.GetManifestResourceStream(
                "MillWorks.AuditCore.EntityFramework.Sql.InstallAuditEventFullText.sql")
                ?? throw new InvalidOperationException("Embedded audit full-text installation SQL is missing.");
            using var reader = new StreamReader(stream);
            // Unsupported engines retain metadata/ID reads and explicit unavailable word search.
            // After installing the feature later, run the same idempotent installer explicitly;
            // a recorded migration must never be edited or re-applied to retrofit the feature.
            migrationBuilder.Sql($"""
                IF FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') = 1
                BEGIN
                {reader.ReadToEnd()}
                END;
                """, suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider != "Microsoft.EntityFrameworkCore.SqlServer") return;
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM sys.fulltext_indexes i
                    JOIN sys.fulltext_catalogs c ON c.fulltext_catalog_id = i.fulltext_catalog_id
                    WHERE i.object_id = OBJECT_ID(N'[audit].[AuditEvents]') AND c.name = N'AuditEventWords')
                    DROP FULLTEXT INDEX ON [audit].[AuditEvents];
                IF EXISTS (SELECT 1 FROM sys.fulltext_catalogs c WHERE c.name = N'AuditEventWords'
                    AND NOT EXISTS (SELECT 1 FROM sys.fulltext_indexes i WHERE i.fulltext_catalog_id = c.fulltext_catalog_id))
                    DROP FULLTEXT CATALOG [AuditEventWords];
                """, suppressTransaction: true);
        }
    }
}
