using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MillWorks.AuditCore.Tests.Services;

/// <summary>
/// Tests for DeadlockDetector covering the SQL Server 1205 and PostgreSQL 40P01 branches
/// and the default false path. Mirrors DuplicateKeyDetectorTests' reflection seam for
/// constructing real SqlException instances; helpers are intentionally duplicated rather
/// than extracted to keep each test file self-contained.
/// </summary>
[TestFixture]
[Category("Unit")]
public class DeadlockDetectorTests
{
    private static readonly System.Reflection.MethodInfo IsDeadlockMethod =
        typeof(MillWorks.AuditCore.Services.DeadlockDetector)
            .GetMethod("IsDeadlock", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;

    private static bool IsDeadlock(DbUpdateException ex)
        => (bool)IsDeadlockMethod.Invoke(null, [ex])!;

    #region SQL Server — Error Number 1205

    [Test]
    public void IsDeadlock_SqlServer1205_ReturnsTrue()
    {
        var sqlEx = CreateSqlException(1205);
        if (sqlEx == null)
        {
            Assert.Ignore("Cannot create SqlException via reflection in this runtime version");
            return;
        }

        var dbEx = new DbUpdateException("Deadlock victim", sqlEx);
        Assert.That(IsDeadlock(dbEx), Is.True);
    }

    [Test]
    public void IsDeadlock_SqlServerNonDeadlock_ReturnsFalse()
    {
        // 2627 is a duplicate-key error, not a deadlock — must NOT match.
        var sqlEx = CreateSqlException(2627);
        if (sqlEx == null)
        {
            Assert.Ignore("Cannot create SqlException via reflection in this runtime version");
            return;
        }

        var dbEx = new DbUpdateException("Duplicate key", sqlEx);
        Assert.That(IsDeadlock(dbEx), Is.False);
    }

    #endregion

    #region PostgreSQL — SqlState 40P01

    [Test]
    public void IsDeadlock_PostgresDeadlockDetected_ReturnsTrue()
    {
        var innerEx = new PostgresException("40P01");
        var dbEx = new DbUpdateException("Postgres deadlock", innerEx);

        Assert.That(IsDeadlock(dbEx), Is.True);
    }

    [Test]
    public void IsDeadlock_PostgresNonDeadlock_ReturnsFalse()
    {
        var innerEx = new PostgresException("23505"); // unique violation, not deadlock
        var dbEx = new DbUpdateException("Postgres unique", innerEx);

        Assert.That(IsDeadlock(dbEx), Is.False);
    }

    #endregion

    #region Default — No Inner Exception / Unknown

    [Test]
    public void IsDeadlock_NullInnerException_ReturnsFalse()
    {
        var dbEx = new DbUpdateException("Error", (Exception?)null);

        Assert.That(IsDeadlock(dbEx), Is.False);
    }

    [Test]
    public void IsDeadlock_GenericInnerException_ReturnsFalse()
    {
        var innerEx = new InvalidOperationException("Something else entirely");
        var dbEx = new DbUpdateException("Error", innerEx);

        Assert.That(IsDeadlock(dbEx), Is.False);
    }

    [Test]
    public void IsDeadlock_SqliteStyleInner_ReturnsFalse()
    {
        // SQLite has no 1205-equivalent; deadlocks are not a SQLite concept.
        var innerEx = new Exception("UNIQUE constraint failed: table.column");
        var dbEx = new DbUpdateException("SQLite error", innerEx);

        Assert.That(IsDeadlock(dbEx), Is.False);
    }

    #endregion

    #region Helpers — duplicated from DuplicateKeyDetectorTests for file isolation

    private static SqlException? CreateSqlException(int number)
    {
        try
        {
            var assembly = typeof(SqlException).Assembly;
            var sqlErrorType = assembly.GetType("Microsoft.Data.SqlClient.SqlError")!;

            var sqlError = TryCreateSqlError(sqlErrorType, number);
            if (sqlError == null) return null;

            var collectionType = assembly.GetType("Microsoft.Data.SqlClient.SqlErrorCollection")!;
            var collection = Activator.CreateInstance(
                collectionType,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null, null, null)!;

            var addMethod = collectionType.GetMethod("Add",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            addMethod?.Invoke(collection, [sqlError]);

            var createMethod = typeof(SqlException).GetMethod(
                "CreateException",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
                null,
                [collectionType, typeof(string)],
                null);

            return createMethod?.Invoke(null, [collection, "10.0"]) as SqlException;
        }
        catch
        {
            return null;
        }
    }

    private static object? TryCreateSqlError(Type sqlErrorType, int number)
    {
        var ctorSignatures = new[]
        {
            new object?[] { number, (byte)0, (byte)0, "server", "", "", 0, (uint)0, null },
            new object?[] { number, (byte)0, (byte)0, "server", "", "", 0, null },
            new object?[] { number, (byte)0, (byte)0, "server", "", "", 0 }
        };

        foreach (var args in ctorSignatures)
        {
            try
            {
                return Activator.CreateInstance(
                    sqlErrorType,
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                    null, args, null);
            }
            catch { /* Try next signature */ }
        }

        return null;
    }

    /// <summary>
    /// Fake exception whose type name is exactly "PostgresException" to match
    /// DeadlockDetector's GetType().Name check. Sets Data["SqlState"].
    /// </summary>
    private sealed class PostgresException : Exception
    {
        public PostgresException(string sqlState) : base("Postgres error")
        {
            Data["SqlState"] = sqlState;
        }
    }

    #endregion
}
