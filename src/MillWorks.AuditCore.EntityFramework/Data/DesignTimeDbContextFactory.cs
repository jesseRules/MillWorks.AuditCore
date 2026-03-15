using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MillWorks.AuditCore.EntityFramework.Data;

/// <summary>
/// Design time factory for AuditApplicationDbContext
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AuditApplicationDbContext>
{
    /// <summary>
    /// Creates a new instance of AuditApplicationDbContext for design-time use
    /// </summary>
    /// <param name="args"></param>
    /// <returns></returns>
    public AuditApplicationDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AuditApplicationDbContext>();
        
        // Try to get connection string from environment variable first
        var connectionString = Environment.GetEnvironmentVariable("AUDIT_MIGRATION_CONNECTION_STRING")
                               ?? "Server=(localdb)\\mssqllocaldb;Database=MillWorksAudit_Migrations;Trusted_Connection=True;MultipleActiveResultSets=true";
        
        optionsBuilder.UseSqlServer(
            connectionString, static sqlOptions => 
            {
                sqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "audit");
            });
        
        return new AuditApplicationDbContext(optionsBuilder.Options);
    }
}