using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MillWorks.AuditCore.Services;

/// <summary>
/// Provider-agnostic deadlock detection for SQL Server and PostgreSQL.
/// Sibling to <see cref="DuplicateKeyDetector"/>; same type-name-match + Data["SqlState"]
/// pattern for PostgreSQL so no Npgsql dependency is introduced.
/// SQLite is excluded — single-process embedded engine has no 1205-equivalent.
/// </summary>
internal static class DeadlockDetector
{
    /// <summary>
    /// Returns true if the exception represents a retryable deadlock-victim outcome.
    /// </summary>
    public static bool IsDeadlock(DbUpdateException ex)
    {
        return ex.InnerException switch
        {
            SqlException { Number: 1205 } => true, // SQL Server deadlock victim
            _ when ex.InnerException?.GetType().Name == "PostgresException"
                   && ex.InnerException.Data["SqlState"]?.ToString() == "40P01" => true, // PostgreSQL
            _ => false
        };
    }
}
