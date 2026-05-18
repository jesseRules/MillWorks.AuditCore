using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MillWorks.AuditCore.EntityFramework.Data;

/// <summary>
/// Design time factory for AuditDbContext
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AuditDbContext>
{
    /// <summary>
    /// Creates a new instance of AuditDbContext for design-time use
    /// </summary>
    /// <param name="args"></param>
    /// <returns></returns>
    public AuditDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AuditDbContext>();
        
        // Try to get connection string from environment variable first
        var connectionString = Environment.GetEnvironmentVariable("AUDIT_MIGRATION_CONNECTION_STRING")
                               ?? "Server=(localdb)\\mssqllocaldb;Database=MillWorksAudit_Migrations;Trusted_Connection=True;MultipleActiveResultSets=true";
        
        optionsBuilder.UseSqlServer(
            connectionString, static sqlOptions => 
            {
                sqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "audit");
            });
        
        return new AuditDbContext(optionsBuilder.Options);
    }
}