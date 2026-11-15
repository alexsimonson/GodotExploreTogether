using Microsoft.Data.Sqlite;

namespace ExploreTogether.Core.Persistence;

/// <summary>
/// One schema step. Either plain SQL, or C# code for changes that reshape existing data
/// (e.g. splitting a column, backfilling values). Each step runs in its own transaction.
/// </summary>
public sealed record Migration(int Version, string Description, string? Sql = null,
    Action<SqliteConnection, SqliteTransaction>? Code = null);

/// <summary>Thrown when a database file was written by a newer version of the server.</summary>
public sealed class DatabaseTooNewException(string path, int fileVersion, int knownVersion)
    : Exception($"Database '{path}' is at schema v{fileVersion}, but this server only knows up to v{knownVersion}. " +
                "Run a newer server build (refusing to open it, to avoid damaging data).")
{
    public int FileVersion { get; } = fileVersion;
    public int KnownVersion { get; } = knownVersion;
}

/// <summary>
/// Ordered schema migrations. PRAGMA user_version records the last applied version.
///
/// Rules:
/// - Add new steps at the end with the next version number.
/// - Data policy (decided 2026-11-15): data isn't precious yet, so shipped steps MAY still be
///   edited during early development. The cost is deleting the dev database (.localdata/server.db).
///   Once real player data matters, shipped steps are frozen: change things only by adding steps.
/// - SQLite can ADD/RENAME/DROP a column, but cannot change a column's type or constraints.
///   For that, create a new table, copy the rows, drop the old table, and rename the new one.
/// </summary>
public static class Migrations
{
    public static readonly IReadOnlyList<Migration> All =
    [
        new(1, "accounts and characters", Sql: """
            CREATE TABLE accounts (
                id             INTEGER PRIMARY KEY,
                username       TEXT NOT NULL UNIQUE COLLATE NOCASE,
                password_hash  TEXT NOT NULL,
                created_at     TEXT NOT NULL,
                last_login_at  TEXT
            );

            CREATE TABLE characters (
                id                 INTEGER PRIMARY KEY,
                account_id         INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                name               TEXT NOT NULL UNIQUE COLLATE NOCASE,
                color              TEXT NOT NULL,
                created_at         TEXT NOT NULL,
                last_played_at     TEXT,
                play_time_seconds  REAL NOT NULL DEFAULT 0,
                hub_x              REAL,
                hub_y              REAL,
                hub_z              REAL,
                hub_yaw            REAL
            );

            CREATE INDEX idx_characters_account ON characters(account_id);
            """),
    ];

    public static int LatestVersion => All.Count;

    /// <summary>
    /// Applies the steps after the database's current version, up to targetVersion (default: all).
    /// Each step commits separately, so a failing step leaves the database at the last good version.
    /// </summary>
    public static void Apply(SqliteConnection connection, IReadOnlyList<Migration> steps, int? targetVersion = null)
    {
        Validate(steps);
        var target = targetVersion ?? steps.Count;
        for (var v = GetVersion(connection) + 1; v <= target; v++)
        {
            var step = steps[v - 1];
            using var tx = connection.BeginTransaction();
            if (step.Sql != null)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = step.Sql;
                cmd.ExecuteNonQuery();
            }
            step.Code?.Invoke(connection, tx);
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"PRAGMA user_version = {v};";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public static int GetVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void Validate(IReadOnlyList<Migration> steps)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            if (s.Version != i + 1)
                throw new InvalidOperationException($"Migration #{i} has version {s.Version}; expected {i + 1} (versions must be 1..N in order).");
            if (s.Sql == null && s.Code == null)
                throw new InvalidOperationException($"Migration v{s.Version} has neither SQL nor code.");
        }
    }
}
