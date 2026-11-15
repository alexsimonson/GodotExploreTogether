using ExploreTogether.Core.Persistence;
using static ExploreTogether.Core.Tests.TempDatabase;

namespace ExploreTogether.Core.Tests;

public class MigrationTests
{
    // A fake two-step history, to exercise the machinery independent of the real schema:
    // v1 stores a full name; v2 (a C# step) splits it into first/last.
    private static readonly IReadOnlyList<Migration> FakeV1 =
    [
        new(1, "people", Sql: "CREATE TABLE people (id INTEGER PRIMARY KEY, full_name TEXT NOT NULL);"),
    ];

    private static readonly IReadOnlyList<Migration> FakeV2 =
    [
        FakeV1[0],
        new(2, "split names", Code: (conn, tx) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                ALTER TABLE people ADD COLUMN first_name TEXT;
                ALTER TABLE people ADD COLUMN last_name TEXT;
                UPDATE people SET
                    first_name = substr(full_name, 1, instr(full_name, ' ') - 1),
                    last_name  = substr(full_name, instr(full_name, ' ') + 1);
                """;
            cmd.ExecuteNonQuery();
        }),
    ];

    [Fact]
    public void Upgrade_KeepsData_AndRunsCodeSteps()
    {
        using var temp = new TempDatabase();
        temp.CreateAtVersion(1, c => Exec(c, "INSERT INTO people (full_name) VALUES ('Kermit Frog');"), FakeV1);

        using (GameDatabase.Open(temp.Path, FakeV2)) { }

        using var conn = GameDatabase.OpenConnection(temp.Path);
        Assert.Equal(2L, Scalar(conn, "PRAGMA user_version;"));
        Assert.Equal("Kermit", Scalar(conn, "SELECT first_name FROM people;"));
        Assert.Equal("Frog", Scalar(conn, "SELECT last_name FROM people;"));
    }

    [Fact]
    public void Upgrade_MakesBackupOfOldVersion()
    {
        using var temp = new TempDatabase();
        temp.CreateAtVersion(1, c => Exec(c, "INSERT INTO people (full_name) VALUES ('Kermit Frog');"), FakeV1);

        string? backup;
        using (var db = GameDatabase.Open(temp.Path, FakeV2))
            backup = db.BackupPath;

        Assert.NotNull(backup);
        Assert.Contains(".v1.", backup);
        Assert.Single(temp.Backups());
        using var conn = GameDatabase.OpenConnection(backup!);
        Assert.Equal(1L, Scalar(conn, "PRAGMA user_version;"));
        Assert.Equal("Kermit Frog", Scalar(conn, "SELECT full_name FROM people;"));
    }

    [Fact]
    public void NoBackup_ForNewDatabase_OrWhenUpToDate()
    {
        using var temp = new TempDatabase();
        using (var db = GameDatabase.Open(temp.Path, FakeV2)) Assert.Null(db.BackupPath); // brand new
        using (var db = GameDatabase.Open(temp.Path, FakeV2)) Assert.Null(db.BackupPath); // up to date
        Assert.Empty(temp.Backups());
    }

    [Fact]
    public void RefusesDatabaseNewerThanCode()
    {
        using var temp = new TempDatabase();
        temp.CreateAtVersion(2, migrations: FakeV2);

        var e = Assert.Throws<DatabaseTooNewException>(() => GameDatabase.Open(temp.Path, FakeV1));
        Assert.Equal(2, e.FileVersion);
        Assert.Equal(1, e.KnownVersion);
    }

    [Fact]
    public void FailingStep_LeavesDatabaseAtLastGoodVersion_WithBackup()
    {
        using var temp = new TempDatabase();
        temp.CreateAtVersion(1, c => Exec(c, "INSERT INTO people (full_name) VALUES ('Kermit Frog');"), FakeV1);
        IReadOnlyList<Migration> broken =
        [
            FakeV1[0],
            new(2, "broken", Sql: "ALTER TABLE people ADD COLUMN x TEXT; THIS IS NOT SQL;"),
        ];

        Assert.ThrowsAny<Exception>(() => GameDatabase.Open(temp.Path, broken));

        using var conn = GameDatabase.OpenConnection(temp.Path);
        Assert.Equal(1L, Scalar(conn, "PRAGMA user_version;"));
        Assert.Equal("Kermit Frog", Scalar(conn, "SELECT full_name FROM people;"));
        Assert.Single(temp.Backups());
    }

    [Fact]
    public void RejectsMisnumberedMigrations()
    {
        using var temp = new TempDatabase();
        IReadOnlyList<Migration> bad = [new(2, "skips v1", Sql: "SELECT 1;")];
        Assert.Throws<InvalidOperationException>(() => GameDatabase.Open(temp.Path, bad));
    }

    [Fact]
    public void KeepsOnlyNewestBackups()
    {
        using var temp = new TempDatabase();
        // Build up a history of upgrades: each round adds one trivial migration.
        var steps = new List<Migration> { FakeV1[0] };
        using (GameDatabase.Open(temp.Path, steps)) { }
        for (var v = 2; v <= 8; v++)
        {
            steps.Add(new Migration(v, $"noop {v}", Sql: "SELECT 1;"));
            using (GameDatabase.Open(temp.Path, steps)) { }
        }
        Assert.Equal(5, temp.Backups().Length);
    }

    // ---- Real schema: one test per historical version, checking data survives to the latest. ----

    [Fact]
    public void RealSchema_V1Data_SurvivesUpgradeToLatest()
    {
        using var temp = new TempDatabase();
        temp.CreateAtVersion(1, c =>
        {
            Exec(c, "INSERT INTO accounts (id, username, password_hash, created_at) VALUES (1, 'kermit', 'h', '2026-11-15T00:00:00.0000000Z');");
            Exec(c, """
                INSERT INTO characters (account_id, name, color, created_at, play_time_seconds, hub_x, hub_y, hub_z, hub_yaw)
                VALUES (1, 'Kermit', '3cb043', '2026-11-15T00:00:00.0000000Z', 42, 1, 2, 3, 0.5);
                """);
        });

        using var db = GameDatabase.Open(temp.Path);
        Assert.Equal(Migrations.LatestVersion, db.SchemaVersion);
        var account = db.FindAccount("kermit")!;
        var character = Assert.Single(db.ListCharacters(account.Id));
        Assert.Equal("Kermit", character.Name);
        Assert.Equal(42, character.PlayTimeSeconds);
        Assert.Equal(new SavedPosition(1, 2, 3, 0.5f), character.HubPosition);
    }
}
