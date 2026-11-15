using ExploreTogether.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace ExploreTogether.Core.Tests;

/// <summary>
/// Upgrade-test helper: builds a database file at an OLD schema version with sample rows,
/// so a test can open it with the current code and check the data survived the upgrade.
///
/// Typical use when adding migration N:
/// <code>
/// using var temp = new TempDatabase();
/// temp.CreateAtVersion(N - 1, conn => Exec(conn, "INSERT INTO ... (columns as they were at v N-1)"));
/// using var db = GameDatabase.Open(temp.Path);
/// // assert the rows look right in the new schema
/// </code>
/// Seed SQL must use the OLD schema's columns. That's the point of the test.
/// </summary>
public sealed class TempDatabase : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"et-test-{Guid.NewGuid():N}", "test.db");

    public string Directory => System.IO.Path.GetDirectoryName(Path)!;

    public TempDatabase() => System.IO.Directory.CreateDirectory(Directory);

    /// <summary>Applies migrations 1..version (from the given list, default the real one), then runs seed.</summary>
    public void CreateAtVersion(int version, Action<SqliteConnection>? seed = null, IReadOnlyList<Migration>? migrations = null)
    {
        using var conn = GameDatabase.OpenConnection(Path);
        Migrations.Apply(conn, migrations ?? Migrations.All, version);
        seed?.Invoke(conn);
    }

    public string[] Backups() => System.IO.Directory.GetFiles(Directory, "*.bak");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch (IOException) { }
    }

    public static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public static object? Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
