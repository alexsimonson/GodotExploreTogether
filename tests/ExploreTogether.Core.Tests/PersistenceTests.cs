using ExploreTogether.Core.Persistence;

namespace ExploreTogether.Core.Tests;

public class PersistenceTests : IDisposable
{
    private readonly GameDatabase _db = GameDatabase.OpenInMemory();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void NewDatabase_IsMigratedToLatest_AndMigrateIsIdempotent()
    {
        Assert.Equal(1, _db.SchemaVersion);
        _db.Migrate();
        Assert.Equal(1, _db.SchemaVersion);
    }

    [Fact]
    public void CreateAccount_RejectsDuplicateUsername_CaseInsensitive()
    {
        Assert.NotNull(_db.CreateAccount("kermit", "h"));
        Assert.Null(_db.CreateAccount("KERMIT", "h"));
    }

    [Fact]
    public void FindAccount_IsCaseInsensitive()
    {
        var created = _db.CreateAccount("Kermit", "hash")!;
        var found = _db.FindAccount("kermit");
        Assert.Equal(created.Id, found?.Id);
        Assert.Null(_db.FindAccount("nobody"));
    }

    [Fact]
    public void CreateCharacter_ListsInOrder_AndStoresColor()
    {
        var acct = _db.CreateAccount("kermit", "h")!;
        Assert.Equal(CreateResult.Created, _db.CreateCharacter(acct.Id, "Kermit", "3cb043").Result);
        Assert.Equal(CreateResult.Created, _db.CreateCharacter(acct.Id, "Gonzo", "4f86f7").Result);

        var list = _db.ListCharacters(acct.Id);
        Assert.Equal(["Kermit", "Gonzo"], list.Select(c => c.Name));
        Assert.Equal("3cb043", list[0].ColorHex);
        Assert.Null(list[0].HubPosition);
        Assert.Equal(0, list[0].PlayTimeSeconds);
    }

    [Fact]
    public void CharacterNames_AreUniqueAcrossAccounts()
    {
        var a = _db.CreateAccount("a_user", "h")!;
        var b = _db.CreateAccount("b_user", "h")!;
        Assert.Equal(CreateResult.Created, _db.CreateCharacter(a.Id, "Kermit", "000000").Result);
        Assert.Equal(CreateResult.NameTaken, _db.CreateCharacter(b.Id, "kermit", "000000").Result);
    }

    [Fact]
    public void CreateCharacter_EnforcesPerAccountLimit()
    {
        var acct = _db.CreateAccount("kermit", "h")!;
        for (int i = 0; i < GameRules.MaxCharactersPerAccount; i++)
            Assert.Equal(CreateResult.Created, _db.CreateCharacter(acct.Id, $"Char{i}", "000000").Result);
        Assert.Equal(CreateResult.LimitReached, _db.CreateCharacter(acct.Id, "OneTooMany", "000000").Result);
    }

    [Fact]
    public void SaveProgress_AccumulatesPlayTime_AndKeepsLastHubPosition()
    {
        var acct = _db.CreateAccount("kermit", "h")!;
        var ch = _db.CreateCharacter(acct.Id, "Kermit", "3cb043").Character!;

        _db.SaveCharacterProgress(ch.Id, 10, new SavedPosition(1, 2, 3, 0.5f));
        _db.SaveCharacterProgress(ch.Id, 5, null); // e.g. saved while not in the hub

        var loaded = _db.GetCharacter(ch.Id)!;
        Assert.Equal(15, loaded.PlayTimeSeconds, 3);
        Assert.Equal(new SavedPosition(1, 2, 3, 0.5f), loaded.HubPosition);
        Assert.NotNull(loaded.LastPlayedAt);
    }

    [Fact]
    public void FileDatabase_PersistsAcrossReopen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"et-test-{Guid.NewGuid():N}.db");
        try
        {
            using (var db = GameDatabase.Open(path))
                db.CreateAccount("kermit", "h");
            using (var db = GameDatabase.Open(path))
            {
                Assert.NotNull(db.FindAccount("kermit"));
                Assert.Equal(1, db.SchemaVersion);
            }
        }
        finally
        {
            foreach (var f in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }
    }
}

public class PasswordHasherTests
{
    [Fact]
    public void Verify_AcceptsCorrect_RejectsWrong()
    {
        var hash = PasswordHasher.Hash("hunter2");
        Assert.True(PasswordHasher.Verify("hunter2", hash));
        Assert.False(PasswordHasher.Verify("hunter3", hash));
    }

    [Fact]
    public void Hash_IsSalted()
    {
        Assert.NotEqual(PasswordHasher.Hash("same"), PasswordHasher.Hash("same"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("pbkdf2-sha256$abc$AAAA$AAAA")]
    [InlineData("pbkdf2-sha256$1000$not-base64!$AAAA")]
    public void Verify_RejectsMalformedHashes(string stored)
    {
        Assert.False(PasswordHasher.Verify("x", stored));
    }
}

public class GameRulesTests
{
    [Theory]
    [InlineData("kermit", true)]
    [InlineData("blue_berry9", true)]
    [InlineData("ab", false)]
    [InlineData("has space", false)]
    [InlineData("toolongusername_123456", false)]
    public void Username(string name, bool valid) => Assert.Equal(valid, GameRules.ValidateUsername(name) == null);

    [Theory]
    [InlineData("Kermit", true)]
    [InlineData("Big Bird", true)]
    [InlineData("Miss-Piggy", true)]
    [InlineData("Ki", false)]
    [InlineData(" Kermit", false)]
    [InlineData("Two  Spaces", false)]
    [InlineData("Bad!", false)]
    public void CharacterName(string name, bool valid) => Assert.Equal(valid, GameRules.ValidateCharacterName(name) == null);

    [Theory]
    [InlineData("#3CB043", "3cb043")]
    [InlineData("3cb043ff", "3cb043")]
    [InlineData("xyz", null)]
    public void NormalizeColorHex(string input, string? expected) => Assert.Equal(expected, GameRules.NormalizeColorHex(input));
}
