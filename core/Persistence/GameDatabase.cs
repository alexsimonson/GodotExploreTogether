using System.Globalization;
using ExploreTogether.Core.Items;
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
        "id, account_id, name, color, created_at, last_played_at, play_time_seconds, hub_x, hub_y, hub_z, hub_yaw, hardcore";

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

    public (CreateResult Result, CharacterRecord? Character) CreateCharacter(long accountId, string name, string colorHex,
        bool hardcore = false)
    {
        using var tx = _connection.BeginTransaction();

        using (var count = Command("SELECT COUNT(*) FROM characters WHERE account_id = $a;", ("$a", accountId)))
        {
            count.Transaction = tx;
            if ((long)count.ExecuteScalar()! >= GameRules.MaxCharactersPerAccount)
                return (CreateResult.LimitReached, null);
        }

        using var insert = Command(
            "INSERT INTO characters (account_id, name, color, created_at, hardcore) VALUES ($a, $n, $c, $now, $h) RETURNING id;",
            ("$a", accountId), ("$n", name), ("$c", colorHex), ("$now", Now()), ("$h", hardcore ? 1 : 0));
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

    /// <summary>
    /// Permanently deletes a character (hardcore death). Its inventory, gear and run participation go with it
    /// (ON DELETE CASCADE); runs it created keep existing with created_by cleared.
    /// </summary>
    public bool DeleteCharacter(long characterId)
    {
        using var cmd = Command("DELETE FROM characters WHERE id = $id;", ("$id", characterId));
        return cmd.ExecuteNonQuery() > 0;
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

    public List<SlotRecord> LoadInventory(long characterId) => LoadSlots("inventory_items", characterId);

    /// <summary>The character's hub storage.</summary>
    public List<SlotRecord> LoadStorage(long characterId) => LoadSlots("storage_items", characterId);

    private List<SlotRecord> LoadSlots(string table, long characterId)
    {
        using var cmd = Command($"SELECT slot, item_id, count, data, uid FROM {table} WHERE character_id = $c ORDER BY slot;",
            ("$c", characterId));
        using var r = cmd.ExecuteReader();
        var list = new List<SlotRecord>();
        while (r.Read())
            list.Add(new SlotRecord(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2),
                r.IsDBNull(3) ? null : r.GetInt32(3), r.IsDBNull(4) ? null : r.GetInt64(4)));
        return list;
    }

    public void SaveInventory(long characterId, IEnumerable<(int Slot, int ItemId, int Count)> slots) =>
        SaveInventory(characterId, slots.Select(s => new SlotRecord(s.Slot, s.ItemId, s.Count)));

    public void SaveInventory(long characterId, IEnumerable<(int Slot, int ItemId, int Count, int? Data)> slots) =>
        SaveInventory(characterId, slots.Select(s => new SlotRecord(s.Slot, s.ItemId, s.Count, s.Data)));

    /// <summary>Replaces the character's saved inventory with these slots.</summary>
    public void SaveInventory(long characterId, IEnumerable<SlotRecord> slots)
    {
        using var tx = _connection.BeginTransaction();
        WriteInventory(tx, characterId, slots);
        tx.Commit();
    }

    /// <summary>
    /// Replaces everything the character carries (inventory and gear), and appends any item events, in ONE
    /// transaction. So a crash can never save one container without the other (which could duplicate an item
    /// that was just unequipped, or lose one that was just equipped), and the event log always matches what
    /// was saved. Anything that moves items between containers must be saved through here.
    /// </summary>
    /// <param name="storage">The hub storage's slots, saved in the same transaction; null leaves the saved storage as it is.</param>
    public void SaveItems(long characterId, IEnumerable<SlotRecord> inventory, IEnumerable<SlotRecord> gear,
        IEnumerable<ItemEvent>? events = null, IEnumerable<SlotRecord>? storage = null)
    {
        using var tx = _connection.BeginTransaction(); // rolled back on dispose unless committed
        WriteGear(tx, characterId, gear);
        WriteInventory(tx, characterId, inventory);
        if (storage != null) WriteSlots(tx, "storage_items", characterId, storage);
        if (events != null) WriteEvents(tx, events);
        tx.Commit();
    }

    private void WriteInventory(SqliteTransaction tx, long characterId, IEnumerable<SlotRecord> slots) =>
        WriteSlots(tx, "inventory_items", characterId, slots);

    private void WriteSlots(SqliteTransaction tx, string table, long characterId, IEnumerable<SlotRecord> slots)
    {
        using (var del = Command($"DELETE FROM {table} WHERE character_id = $c;", ("$c", characterId)))
        {
            del.Transaction = tx;
            del.ExecuteNonQuery();
        }
        foreach (var s in slots)
        {
            using var ins = Command(
                $"INSERT INTO {table} (character_id, slot, item_id, count, data, uid) VALUES ($c, $s, $i, $n, $d, $u);",
                ("$c", characterId), ("$s", s.Slot), ("$i", s.ItemId), ("$n", s.Count), ("$d", s.Data), ("$u", s.Uid));
            ins.Transaction = tx;
            ins.ExecuteNonQuery();
        }
    }

    // ------------------------------------------------------------- equipment

    /// <summary>Saved gear as an item id per slot (-1 = empty), like the original ExportGear.</summary>
    public int[] LoadGear(long characterId, int slotCount) => LoadGearWithData(characterId, slotCount).Ids;

    /// <summary>Saved gear: item id and item state per slot (-1 = empty / none).</summary>
    public (int[] Ids, int[] Data) LoadGearWithData(long characterId, int slotCount)
    {
        var gear = Enumerable.Repeat(-1, slotCount).ToArray();
        var data = Enumerable.Repeat(-1, slotCount).ToArray();
        foreach (var r in LoadGearRecords(characterId))
        {
            if (r.Slot < 0 || r.Slot >= slotCount) continue;
            gear[r.Slot] = r.ItemId;
            if (r.Data is { } d) data[r.Slot] = d;
        }
        return (gear, data);
    }

    /// <summary>Saved gear as records (Slot = gear slot index), including serials.</summary>
    public List<SlotRecord> LoadGearRecords(long characterId)
    {
        using var cmd = Command("SELECT slot, item_id, data, uid FROM equipment_items WHERE character_id = $c ORDER BY slot;",
            ("$c", characterId));
        using var r = cmd.ExecuteReader();
        var list = new List<SlotRecord>();
        while (r.Read())
            list.Add(new SlotRecord(r.GetInt32(0), r.GetInt32(1), 1,
                r.IsDBNull(2) ? null : r.GetInt32(2), r.IsDBNull(3) ? null : r.GetInt64(3)));
        return list;
    }

    /// <summary>Saves gear given as an item id per slot (-1 = empty) and optional state per slot (-1 = none).</summary>
    public void SaveGear(long characterId, IReadOnlyList<int> gearItemIds, IReadOnlyList<int>? gearData = null)
    {
        var records = new List<SlotRecord>();
        for (int slot = 0; slot < gearItemIds.Count; slot++)
        {
            if (gearItemIds[slot] < 0) continue;
            var d = gearData != null && slot < gearData.Count && gearData[slot] >= 0 ? gearData[slot] : (int?)null;
            records.Add(new SlotRecord(slot, gearItemIds[slot], 1, d));
        }
        using var tx = _connection.BeginTransaction();
        WriteGear(tx, characterId, records);
        tx.Commit();
    }

    private void WriteGear(SqliteTransaction tx, long characterId, IEnumerable<SlotRecord> gear)
    {
        using (var del = Command("DELETE FROM equipment_items WHERE character_id = $c;", ("$c", characterId)))
        {
            del.Transaction = tx;
            del.ExecuteNonQuery();
        }
        foreach (var g in gear)
        {
            using var ins = Command("INSERT INTO equipment_items (character_id, slot, item_id, data, uid) VALUES ($c, $s, $i, $d, $u);",
                ("$c", characterId), ("$s", g.Slot), ("$i", g.ItemId), ("$d", g.Data), ("$u", g.Uid));
            ins.Transaction = tx;
            ins.ExecuteNonQuery();
        }
    }

    // ------------------------------------------------------- item tracking

    /// <summary>
    /// Registers a new copy of a non-stackable item and returns its serial (uid). Origin says where it came
    /// from, e.g. "loot, run 12" or "dev starter kit".
    /// </summary>
    public long RegisterItem(int itemId, string origin)
    {
        using var cmd = Command("INSERT INTO item_serials (item_id, created_at, origin) VALUES ($i, $now, $o) RETURNING uid;",
            ("$i", itemId), ("$now", Now()), ("$o", origin));
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>Where a serial came from, or null if the registry never issued it.</summary>
    public (int ItemId, string CreatedAt, string Origin)? GetItemSerial(long uid)
    {
        using var cmd = Command("SELECT item_id, created_at, origin FROM item_serials WHERE uid = $u;", ("$u", uid));
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt32(0), r.GetString(1), r.GetString(2)) : null;
    }

    /// <summary>Appends events to the item log on their own (when nothing else is being saved).</summary>
    public void LogItemEvents(IEnumerable<ItemEvent> events)
    {
        using var tx = _connection.BeginTransaction();
        WriteEvents(tx, events);
        tx.Commit();
    }

    private void WriteEvents(SqliteTransaction tx, IEnumerable<ItemEvent> events)
    {
        foreach (var e in events)
        {
            using var ins = Command("""
                INSERT INTO item_events (at, character_id, character_name, action, item_id, count, uid, instance_id, run_id, detail)
                VALUES ($at, $c, $n, $a, $i, $k, $u, $inst, $run, $d);
                """,
                ("$at", Now()), ("$c", e.CharacterId), ("$n", e.CharacterName), ("$a", e.Action), ("$i", e.ItemId),
                ("$k", e.Count), ("$u", e.Uid), ("$inst", e.InstanceId), ("$run", e.RunId), ("$d", e.Detail));
            ins.Transaction = tx;
            ins.ExecuteNonQuery();
        }
    }

    /// <summary>Everything logged about one serial, oldest first.</summary>
    public List<ItemEventRecord> GetItemHistory(long uid) =>
        ReadEvents("WHERE uid = $u ORDER BY id;", ("$u", uid));

    /// <summary>A character's most recent item events (looked up by name, so deleted characters work too), oldest first.</summary>
    public List<ItemEventRecord> GetCharacterItemHistory(string characterName, int limit = 200) =>
        ReadEvents("""
            WHERE id IN (SELECT id FROM item_events WHERE character_name = $n COLLATE NOCASE ORDER BY id DESC LIMIT $l)
            ORDER BY id;
            """, ("$n", characterName), ("$l", limit));

    private List<ItemEventRecord> ReadEvents(string where, params (string Name, object? Value)[] parameters)
    {
        using var cmd = Command(
            "SELECT id, at, character_id, character_name, action, item_id, count, uid, instance_id, run_id, detail FROM item_events " + where,
            parameters);
        using var r = cmd.ExecuteReader();
        var list = new List<ItemEventRecord>();
        while (r.Read())
            list.Add(new ItemEventRecord(r.GetInt64(0), r.GetString(1), new ItemEvent(
                Action: r.GetString(4), ItemId: r.GetInt32(5), Count: r.GetInt32(6),
                Uid: r.IsDBNull(7) ? null : r.GetInt64(7),
                CharacterId: r.IsDBNull(2) ? null : r.GetInt64(2),
                CharacterName: r.IsDBNull(3) ? null : r.GetString(3),
                InstanceId: r.IsDBNull(8) ? null : r.GetInt32(8),
                RunId: r.IsDBNull(9) ? null : r.GetInt64(9),
                Detail: r.IsDBNull(10) ? null : r.GetString(10))));
        return list;
    }

    /// <summary>
    /// Checks every saved item that carries a serial and reports problems:
    /// - Duplicate: the same serial is saved in more than one place (inventory, gear or storage, any character).
    /// - Unregistered: the registry never issued that serial (made up, or its registry row was lost).
    /// - WrongItem: the serial was issued for a different item than the one carrying it.
    /// </summary>
    public List<ItemAuditProblem> AuditItems()
    {
        var places = new List<(long Uid, int ItemId, string Where)>();
        using (var cmd = Command("""
            SELECT i.uid, i.item_id, c.name || ' (#' || c.id || ') inventory slot ' || i.slot
              FROM inventory_items i JOIN characters c ON c.id = i.character_id WHERE i.uid IS NOT NULL
            UNION ALL
            SELECT e.uid, e.item_id, c.name || ' (#' || c.id || ') gear slot ' || e.slot
              FROM equipment_items e JOIN characters c ON c.id = e.character_id WHERE e.uid IS NOT NULL
            UNION ALL
            SELECT t.uid, t.item_id, c.name || ' (#' || c.id || ') storage slot ' || t.slot
              FROM storage_items t JOIN characters c ON c.id = t.character_id WHERE t.uid IS NOT NULL;
            """))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                places.Add((r.GetInt64(0), r.GetInt32(1), r.GetString(2)));

        var registry = new Dictionary<long, int>();
        using (var cmd = Command("SELECT uid, item_id FROM item_serials;"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) registry[r.GetInt64(0)] = r.GetInt32(1);

        var problems = new List<ItemAuditProblem>();
        foreach (var group in places.GroupBy(p => p.Uid).OrderBy(g => g.Key))
        {
            var where = group.Select(p => p.Where).ToList();
            var itemId = group.First().ItemId;
            if (group.Count() > 1)
                problems.Add(new ItemAuditProblem(ItemAuditKind.Duplicate, group.Key, itemId, where));
            if (!registry.TryGetValue(group.Key, out var registeredItem))
                problems.Add(new ItemAuditProblem(ItemAuditKind.Unregistered, group.Key, itemId, where));
            else if (group.Any(p => p.ItemId != registeredItem))
                problems.Add(new ItemAuditProblem(ItemAuditKind.WrongItem, group.Key, itemId, where));
        }
        return problems;
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
            r.GetDouble(6), hub, r.GetInt64(11) != 0);
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
