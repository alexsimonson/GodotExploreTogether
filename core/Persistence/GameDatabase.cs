using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ExploreTogether.Core.Persistence;

/// <summary>
/// The server's SQLite database. Not thread-safe: use it from one thread (the game thread).
/// </summary>
public sealed class GameDatabase : IDisposable
{
    private const int SqliteConstraint = 19;
    private const int BackupsToKeep = 5;

    private readonly SqliteConnection _connection;
    private readonly IReadOnlyList<Migration> _migrations;

    private GameDatabase(SqliteConnection connection, IReadOnlyList<Migration> migrations)
    {
        _connection = connection;
        _migrations = migrations;
        Exec("PRAGMA foreign_keys = ON;");
    }

    /// <summary>
    /// Opens (creating if needed) a database file and brings it to the latest schema:
    /// - refuses a file newer than this code knows (<see cref="DatabaseTooNewException"/>);
    /// - if an existing file needs upgrading, backs it up first (see <see cref="BackupPath"/>);
    /// - applies pending migrations.
    /// </summary>
    public static GameDatabase Open(string path) => Open(path, Migrations.All);

    /// <summary>Open with a custom migration list (for tests).</summary>
    public static GameDatabase Open(string path, IReadOnlyList<Migration> migrations)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var connection = OpenConnection(fullPath);
        try
        {
            var db = new GameDatabase(connection, migrations);
            var current = Migrations.GetVersion(connection);
            if (current > migrations.Count)
                throw new DatabaseTooNewException(fullPath, current, migrations.Count);
            if (current > 0 && current < migrations.Count)
                db.BackupPath = db.Backup(fullPath, current);
            Migrations.Apply(connection, migrations);
            db.Exec("PRAGMA journal_mode = WAL;");
            return db;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>An in-memory database (for tests). It lives as long as this object.</summary>
    public static GameDatabase OpenInMemory()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var db = new GameDatabase(connection, Migrations.All);
        Migrations.Apply(connection, Migrations.All);
        return db;
    }

    /// <summary>Opens a raw connection to a database file (for tests and tools that seed old schemas).</summary>
    public static SqliteConnection OpenConnection(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    public int SchemaVersion => Migrations.GetVersion(_connection);

    /// <summary>The backup made before upgrading on open, or null if none was needed.</summary>
    public string? BackupPath { get; private set; }

    /// <summary>Re-runs migrations (a no-op when up to date).</summary>
    public void Migrate() => Migrations.Apply(_connection, _migrations);

    public void Dispose() => _connection.Dispose();

    /// <summary>
    /// Copies the database next to itself as "&lt;name&gt;.v{version}.{timestamp}.bak" using SQLite's
    /// online backup (consistent even with WAL), then prunes to the newest few backups.
    /// </summary>
    private string Backup(string dbPath, int version)
    {
        var dir = Path.GetDirectoryName(dbPath)!;
        var name = Path.GetFileName(dbPath);
        var backupPath = Path.Combine(dir, $"{name}.v{version}.{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.bak");
        using (var dest = OpenConnection(backupPath))
            _connection.BackupDatabase(dest);

        var old = Directory.GetFiles(dir, $"{name}.v*.bak")
            .OrderByDescending(File.GetCreationTimeUtc).ThenByDescending(f => f)
            .Skip(BackupsToKeep);
        foreach (var f in old) File.Delete(f);
        return backupPath;
    }

    // ---------------------------------------------------------------- accounts

    /// <summary>Creates an account. Returns null if the username is taken (case-insensitive).</summary>
    public AccountRecord? CreateAccount(string username, string passwordHash)
    {
        using var cmd = Command(
            "INSERT INTO accounts (username, password_hash, created_at) VALUES ($u, $h, $now) RETURNING id;",
            ("$u", username), ("$h", passwordHash), ("$now", Now()));
        try
        {
            var id = (long)cmd.ExecuteScalar()!;
            return new AccountRecord(id, username, passwordHash);
        }
        catch (SqliteException e) when (e.SqliteErrorCode == SqliteConstraint)
        {
            return null;
        }
    }

    public AccountRecord? FindAccount(string username)
    {
        using var cmd = Command("SELECT id, username, password_hash FROM accounts WHERE username = $u;", ("$u", username));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new AccountRecord(r.GetInt64(0), r.GetString(1), r.GetString(2)) : null;
    }

    public void RecordLogin(long accountId)
    {
        using var cmd = Command("UPDATE accounts SET last_login_at = $now WHERE id = $id;", ("$now", Now()), ("$id", accountId));
        cmd.ExecuteNonQuery();
    }

    // -------------------------------------------------------------- characters

    private const string CharacterColumns =
        "id, account_id, name, color, created_at, last_played_at, play_time_seconds, hub_x, hub_y, hub_z, hub_yaw";

    public IReadOnlyList<CharacterRecord> ListCharacters(long accountId)
    {
        using var cmd = Command($"SELECT {CharacterColumns} FROM characters WHERE account_id = $a ORDER BY id;", ("$a", accountId));
        using var r = cmd.ExecuteReader();
        var list = new List<CharacterRecord>();
        while (r.Read()) list.Add(ReadCharacter(r));
        return list;
    }

    public CharacterRecord? GetCharacter(long characterId)
    {
        using var cmd = Command($"SELECT {CharacterColumns} FROM characters WHERE id = $id;", ("$id", characterId));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadCharacter(r) : null;
    }

    public (CreateResult Result, CharacterRecord? Character) CreateCharacter(long accountId, string name, string colorHex)
    {
        using var tx = _connection.BeginTransaction();

        using (var count = Command("SELECT COUNT(*) FROM characters WHERE account_id = $a;", ("$a", accountId)))
        {
            count.Transaction = tx;
            if ((long)count.ExecuteScalar()! >= GameRules.MaxCharactersPerAccount)
                return (CreateResult.LimitReached, null);
        }

        using var insert = Command(
            "INSERT INTO characters (account_id, name, color, created_at) VALUES ($a, $n, $c, $now) RETURNING id;",
            ("$a", accountId), ("$n", name), ("$c", colorHex), ("$now", Now()));
        insert.Transaction = tx;
        try
        {
            var id = (long)insert.ExecuteScalar()!;
            tx.Commit();
            return (CreateResult.Created, GetCharacter(id));
        }
        catch (SqliteException e) when (e.SqliteErrorCode == SqliteConstraint)
        {
            return (CreateResult.NameTaken, null);
        }
    }

    /// <summary>Saves progress: adds play time, stamps last played, and stores the hub position if given.</summary>
    public void SaveCharacterProgress(long characterId, double addPlayTimeSeconds, SavedPosition? hubPosition)
    {
        using var cmd = Command(
            """
            UPDATE characters SET
                play_time_seconds = play_time_seconds + $dt,
                last_played_at = $now,
                hub_x = COALESCE($x, hub_x), hub_y = COALESCE($y, hub_y),
                hub_z = COALESCE($z, hub_z), hub_yaw = COALESCE($yaw, hub_yaw)
            WHERE id = $id;
            """,
            ("$dt", addPlayTimeSeconds), ("$now", Now()), ("$id", characterId),
            ("$x", hubPosition?.X), ("$y", hubPosition?.Y), ("$z", hubPosition?.Z), ("$yaw", hubPosition?.Yaw));
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------- inventory

    public List<(int Slot, int ItemId, int Count)> LoadInventory(long characterId)
    {
        using var cmd = Command("SELECT slot, item_id, count FROM inventory_items WHERE character_id = $c ORDER BY slot;",
            ("$c", characterId));
        using var r = cmd.ExecuteReader();
        var list = new List<(int, int, int)>();
        while (r.Read()) list.Add((r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)));
        return list;
    }

    /// <summary>Replaces the character's saved inventory with these slots.</summary>
    public void SaveInventory(long characterId, IEnumerable<(int Slot, int ItemId, int Count)> slots)
    {
        using var tx = _connection.BeginTransaction();
        using (var del = Command("DELETE FROM inventory_items WHERE character_id = $c;", ("$c", characterId)))
        {
            del.Transaction = tx;
            del.ExecuteNonQuery();
        }
        foreach (var (slot, itemId, count) in slots)
        {
            using var ins = Command("INSERT INTO inventory_items (character_id, slot, item_id, count) VALUES ($c, $s, $i, $n);",
                ("$c", characterId), ("$s", slot), ("$i", itemId), ("$n", count));
            ins.Transaction = tx;
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // ------------------------------------------------------------------ runs

    public long CreateRun(int seed, int width, int height, int length, long? createdByCharacterId)
    {
        using var cmd = Command(
            "INSERT INTO runs (seed, width, height, length, created_by, started_at) VALUES ($s, $w, $h, $l, $c, $now) RETURNING id;",
            ("$s", seed), ("$w", width), ("$h", height), ("$l", length), ("$c", createdByCharacterId), ("$now", Now()));
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>Records that a character took part (no-op if already recorded, e.g. after rejoining).</summary>
    public void AddRunParticipant(long runId, long characterId)
    {
        using var cmd = Command(
            "INSERT OR IGNORE INTO run_participants (run_id, character_id, joined_at) VALUES ($r, $c, $now);",
            ("$r", runId), ("$c", characterId), ("$now", Now()));
        cmd.ExecuteNonQuery();
    }

    public void MarkEscaped(long runId, long characterId)
    {
        using var cmd = Command(
            "UPDATE run_participants SET escaped_at = COALESCE(escaped_at, $now) WHERE run_id = $r AND character_id = $c;",
            ("$now", Now()), ("$r", runId), ("$c", characterId));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Ends a run once (later calls are ignored). Outcome: "completed", "abandoned" or "interrupted".</summary>
    public void EndRun(long runId, string outcome)
    {
        using var cmd = Command("UPDATE runs SET ended_at = $now, outcome = $o WHERE id = $r AND ended_at IS NULL;",
            ("$now", Now()), ("$o", outcome), ("$r", runId));
        cmd.ExecuteNonQuery();
    }

    public (string? Outcome, int Participants, int Escaped) GetRunSummary(long runId)
    {
        using var cmd = Command(
            """
            SELECT r.outcome,
                   (SELECT COUNT(*) FROM run_participants p WHERE p.run_id = r.id),
                   (SELECT COUNT(*) FROM run_participants p WHERE p.run_id = r.id AND p.escaped_at IS NOT NULL)
            FROM runs r WHERE r.id = $r;
            """, ("$r", runId));
        using var rd = cmd.ExecuteReader();
        return rd.Read() ? (rd.IsDBNull(0) ? null : rd.GetString(0), rd.GetInt32(1), rd.GetInt32(2)) : (null, 0, 0);
    }

    // ----------------------------------------------------------------- helpers

    private static CharacterRecord ReadCharacter(SqliteDataReader r)
    {
        SavedPosition? hub = r.IsDBNull(7)
            ? null
            : new SavedPosition((float)r.GetDouble(7), (float)r.GetDouble(8), (float)r.GetDouble(9), (float)r.GetDouble(10));
        return new CharacterRecord(
            r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3),
            ParseTime(r.GetString(4)), r.IsDBNull(5) ? null : ParseTime(r.GetString(5)),
            r.GetDouble(6), hub);
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private void Exec(string sql)
    {
        using var cmd = Command(sql);
        cmd.ExecuteNonQuery();
    }

    private static string Now() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static DateTime ParseTime(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
