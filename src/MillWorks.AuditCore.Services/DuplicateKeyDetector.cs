using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MillWorks.AuditCore.Services;

/// <summary>
/// Provider-agnostic duplicate key detection for SQL Server, SQLite, and PostgreSQL.
/// </summary>
internal static class DuplicateKeyDetector
{
    /// <summary>
    /// Returns true if the exception represents a duplicate key / unique constraint violation.
    /// </summary>
    public static bool IsDuplicateKey(DbUpdateException ex)
    {
        return ex.InnerException switch
        {
            SqlException { Number: 2627 or 2601 } => true, // SQL Server
            _ when ex.InnerException?.Message.Contains("UNIQUE constraint") == true => true, // SQLite
            _ when ex.InnerException?.GetType().Name == "PostgresException"
                   && ex.InnerException.Data["SqlState"]?.ToString() == "23505" => true, // PostgreSQL
            _ => false
        };
    }
}
