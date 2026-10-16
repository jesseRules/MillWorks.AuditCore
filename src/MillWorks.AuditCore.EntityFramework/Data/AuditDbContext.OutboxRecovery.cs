using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MillWorks.AuditCore.EntityFramework.Entities;

namespace MillWorks.AuditCore.EntityFramework.Data;

public partial class AuditDbContext
{
    private void ConfigureOutboxRecovery(ModelBuilder modelBuilder, bool isSqlite)
    {
        modelBuilder.Entity<AuditOutboxOperation>(e =>
        {
            e.ToTable("AuditOutboxOperations", _schema);
            e.HasIndex(x => x.VerifyUntil);
        });
        modelBuilder.Entity<AuditOutboxFailureHandoff>(e =>
        {
            e.ToTable("AuditOutboxFailureHandoffs", _schema);
            // A replayed source can fail again; old Delivered cycles must not acknowledge it.
            e.HasIndex(x => new { x.OutboxRowId, x.FailureCycleId }).IsUnique();
            e.HasOne<AuditOutboxEntity>().WithMany().HasForeignKey(x => x.OutboxRowId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.Status, x.NextRetryAt, x.LeaseExpiresAt, x.CreatedAt });
        });
        modelBuilder.Entity<AuditOutboxResolution>(e =>
        {
            e.ToTable("AuditOutboxResolutions", _schema);
            e.HasIndex(x => new { x.OutboxRowId, x.FailureCycleId });
        });

        if (!isSqlite) return;
        // Use the provider's native TEXT timestamp format so consumer-context/raw-SQL
        // outbox inserts remain readable. Producer scheduling timestamps are UTC; normalize
        // writes here too, allowing SQLite to compare/order the mapped strings.
        var converter = new ValueConverter<DateTimeOffset, string>(
            x => x.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
            x => DateTimeOffset.Parse(x, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal));
        foreach (var type in new[] { typeof(AuditOutboxEntity), typeof(AuditOutboxOperation),
                     typeof(AuditOutboxFailureHandoff), typeof(AuditOutboxResolution) })
        foreach (var property in modelBuilder.Entity(type).Metadata.GetProperties())
            if ((Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType) == typeof(DateTimeOffset))
                property.SetValueConverter(converter);
    }
}
