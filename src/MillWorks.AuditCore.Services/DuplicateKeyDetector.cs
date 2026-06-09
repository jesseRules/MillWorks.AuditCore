using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MillWorks.AuditCore.Services;

/// <summary>
/// Provider-agnostic duplicate key detection for SQL Server, SQLite, and PostgreSQL.
/// Supports both EF's DbUpdateException (from SaveChanges) and raw provider exceptions
/// (from ExecuteSqlRawAsync).
/// </summary>
internal static class DuplicateKeyDetector
{
    /// <summary>
    /// Returns true if the exception represents a duplicate key / unique constraint violation.
    /// Works for DbUpdateException from SaveChanges pipelines.
    /// </summary>
    public static bool IsDuplicateKey(DbUpdateException ex)
    {
        return IsProviderDuplicateKey(ex.InnerException);
    }

    /// <summary>
    /// Returns true if the exception represents a duplicate key / unique constraint violation.
    /// Works for any exception, including raw provider exceptions from ExecuteSqlRawAsync.
    /// </summary>
    public static bool IsDuplicateKey(Exception? ex)
    {
        return ex switch
        {
            null => false,
            DbUpdateException dbEx => IsDuplicateKey(dbEx),
            _ => IsProviderDuplicateKey(ex)
        };
    }

    private static bool IsProviderDuplicateKey(Exception? ex)
    {
        return ex switch
        {
            SqlException { Number: 2627 or 2601 } => true, // SQL Server PK/unique violation
            _ when ex?.Message.Contains("UNIQUE constraint") == true => true, // SQLite
            _ when ex?.GetType().Name == "PostgresException"
                   && ex.Data["SqlState"]?.ToString() == "23505" => true, // PostgreSQL
            _ => false
        };
    }
}
