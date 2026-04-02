using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MillWorks.AuditCore.Tests.Services;

/// <summary>
/// Tests for DuplicateKeyDetector covering all three database provider branches
/// (SQL Server, SQLite, PostgreSQL) and the default false path.
/// </summary>
[TestFixture]
[Category("Unit")]
public class DuplicateKeyDetectorTests
{
    // DuplicateKeyDetector is internal, so we test it through TamperDetectionService's
    // retry behavior. But we can also test it directly via InternalsVisibleTo or reflection.
    // Since it's static with a single public method, reflection is simplest.

    private static readonly System.Reflection.MethodInfo IsDuplicateKeyMethod =
        typeof(MillWorks.AuditCore.Services.DuplicateKeyDetector)
            .GetMethod("IsDuplicateKey", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;

    private static bool IsDuplicateKey(DbUpdateException ex)
        => (bool)IsDuplicateKeyMethod.Invoke(null, [ex])!;

    #region SQL Server — Error Numbers 2627 and 2601

    [Test]
    public void IsDuplicateKey_SqlServer2627_ReturnsTrue()
    {
        var sqlEx = CreateSqlException(2627);
        if (sqlEx == null)
        {
            Assert.Ignore("Cannot create SqlException via reflection in this runtime version");
            return;
        }

        var dbEx = new DbUpdateException("Duplicate key", sqlEx);
        Assert.That(IsDuplicateKey(dbEx), Is.True);
    }

    [Test]
    public void IsDuplicateKey_SqlServer2601_ReturnsTrue()
    {
        var sqlEx = CreateSqlException(2601);
        if (sqlEx == null)
        {
            Assert.Ignore("Cannot create SqlException via reflection in this runtime version");
            return;
        }

        var dbEx = new DbUpdateException("Duplicate key", sqlEx);
        Assert.That(IsDuplicateKey(dbEx), Is.True);
    }

    [Test]
    public void IsDuplicateKey_SqlServerOtherError_ReturnsFalse()
    {
        var sqlEx = CreateSqlException(547);
        if (sqlEx == null)
        {
            Assert.Ignore("Cannot create SqlException via reflection in this runtime version");
            return;
        }

        var dbEx = new DbUpdateException("FK violation", sqlEx);
        Assert.That(IsDuplicateKey(dbEx), Is.False);
    }

    #endregion

    #region SQLite — UNIQUE constraint message

    [Test]
    public void IsDuplicateKey_SqliteUniqueConstraint_ReturnsTrue()
    {
        var innerEx = new Exception("UNIQUE constraint failed: table.column");
        var dbEx = new DbUpdateException("SQLite error", innerEx);

        Assert.That(IsDuplicateKey(dbEx), Is.True);
    }

    [Test]
    public void IsDuplicateKey_SqliteOtherError_ReturnsFalse()
    {
        var innerEx = new Exception("NOT NULL constraint failed: table.column");
        var dbEx = new DbUpdateException("SQLite error", innerEx);

        Assert.That(IsDuplicateKey(dbEx), Is.False);
    }

    #endregion

    #region PostgreSQL — SqlState 23505

    [Test]
    public void IsDuplicateKey_PostgresUniqueViolation_ReturnsTrue()
    {
        // Create a fake PostgresException-like object with the right SqlState
        var innerEx = new PostgresException("23505");
        var dbEx = new DbUpdateException("Postgres duplicate", innerEx);

        Assert.That(IsDuplicateKey(dbEx), Is.True);
    }

    [Test]
    public void IsDuplicateKey_PostgresOtherError_ReturnsFalse()
    {
        var innerEx = new PostgresException("23503"); // FK violation
        var dbEx = new DbUpdateException("Postgres FK", innerEx);

        Assert.That(IsDuplicateKey(dbEx), Is.False);
    }

    #endregion

    #region Default — No Inner Exception / Unknown

    [Test]
    public void IsDuplicateKey_NullInnerException_ReturnsFalse()
    {
        var dbEx = new DbUpdateException("Error", (Exception?)null);

        Assert.That(IsDuplicateKey(dbEx), Is.False);
    }

    [Test]
    public void IsDuplicateKey_GenericInnerException_ReturnsFalse()
    {
        var innerEx = new InvalidOperationException("Something else entirely");
        var dbEx = new DbUpdateException("Error", innerEx);

        Assert.That(IsDuplicateKey(dbEx), Is.False);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Attempts to create a SqlException via reflection. Returns null if the internal
    /// constructor has changed in this version of Microsoft.Data.SqlClient.
    /// </summary>
    private static SqlException? CreateSqlException(int number)
    {
        try
        {
            var assembly = typeof(SqlException).Assembly;
            var sqlErrorType = assembly.GetType("Microsoft.Data.SqlClient.SqlError")!;

            // Try different constructor signatures across MDS versions
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
        // Try all known constructor signatures
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
    /// DuplicateKeyDetector's GetType().Name check. Sets Data["SqlState"].
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
