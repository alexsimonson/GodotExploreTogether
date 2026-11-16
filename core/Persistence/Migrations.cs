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

        new(2, "inventories and dungeon runs", Sql: """
            CREATE TABLE inventory_items (
                character_id  INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
                slot          INTEGER NOT NULL,
                item_id       INTEGER NOT NULL,
                count         INTEGER NOT NULL,
                PRIMARY KEY (character_id, slot)
            );

            CREATE TABLE runs (
                id          INTEGER PRIMARY KEY,
                seed        INTEGER NOT NULL,
                width       INTEGER NOT NULL,
                height      INTEGER NOT NULL,
                length      INTEGER NOT NULL,
                created_by  INTEGER REFERENCES characters(id) ON DELETE SET NULL,
                started_at  TEXT NOT NULL,
                ended_at    TEXT,
                outcome     TEXT
            );

            CREATE TABLE run_participants (
                run_id        INTEGER NOT NULL REFERENCES runs(id) ON DELETE CASCADE,
                character_id  INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
                joined_at     TEXT NOT NULL,
                escaped_at    TEXT,
                PRIMARY KEY (run_id, character_id)
            );
            """),

        new(3, "equipment (gear)", Sql: """
            CREATE TABLE equipment_items (
                character_id  INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
                slot          INTEGER NOT NULL,
                item_id       INTEGER NOT NULL,
                PRIMARY KEY (character_id, slot)
            );
            """),

        new(4, "hardcore characters", Sql: """
            ALTER TABLE characters ADD COLUMN hardcore INTEGER NOT NULL DEFAULT 0;
            """),

        new(5, "per-item state (a gun's loaded rounds)", Sql: """
            ALTER TABLE inventory_items ADD COLUMN data INTEGER;
            ALTER TABLE equipment_items ADD COLUMN data INTEGER;
            """),

        // Duplication tracing (see docs/ITEM-SECURITY-REVIEW-2026-11-15.md, finding 8).
        // - item_serials: every non-stackable copy ever created, with where it came from. Its uid is the
        //   serial stored on the copy, so the same uid in two places means a duplicate.
        // - item_events: append-only history of item movements. Deliberately NOT linked to characters
        //   with a foreign key, so the history survives a character being deleted.
        // The uid indexes aren't UNIQUE on purpose: a duplicate must be detected and reported, not make
        // the owner's saves fail.
        new(6, "item serials and item event log", Sql: """
            ALTER TABLE inventory_items ADD COLUMN uid INTEGER;
            ALTER TABLE equipment_items ADD COLUMN uid INTEGER;
            CREATE INDEX idx_inventory_items_uid ON inventory_items(uid) WHERE uid IS NOT NULL;
            CREATE INDEX idx_equipment_items_uid ON equipment_items(uid) WHERE uid IS NOT NULL;

            CREATE TABLE item_serials (
                uid         INTEGER PRIMARY KEY AUTOINCREMENT,
                item_id     INTEGER NOT NULL,
                created_at  TEXT NOT NULL,
                origin      TEXT NOT NULL
            );

            CREATE TABLE item_events (
                id              INTEGER PRIMARY KEY,
                at              TEXT NOT NULL,
                character_id    INTEGER,
                character_name  TEXT,
                action          TEXT NOT NULL,
                item_id         INTEGER NOT NULL,
                count           INTEGER NOT NULL,
                uid             INTEGER,
                instance_id     INTEGER,
                run_id          INTEGER,
                detail          TEXT
            );
            CREATE INDEX idx_item_events_uid ON item_events(uid) WHERE uid IS NOT NULL;
            CREATE INDEX idx_item_events_character ON item_events(character_id);
            """),

        // Hub storage (port of the original's CharacterData.hub_storage_inventory): per character, same
        // columns as inventory_items. Deleted with the character, like the rest of what it owns.
        new(7, "hub storage", Sql: """
            CREATE TABLE storage_items (
                character_id  INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
                slot          INTEGER NOT NULL,
                item_id       INTEGER NOT NULL,
                count         INTEGER NOT NULL,
                data          INTEGER,
                uid           INTEGER,
                PRIMARY KEY (character_id, slot)
            );
            CREATE INDEX idx_storage_items_uid ON storage_items(uid) WHERE uid IS NOT NULL;
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
