using ExploreTogether.Core.Combat;
using ExploreTogether.Core.Dungeon;
using ExploreTogether.Core.Items;
using ExploreTogether.Core.Persistence;
using static ExploreTogether.Core.Tests.TempDatabase;

namespace ExploreTogether.Core.Tests;

public class HealthAndAmmoTests
{
    [Fact]
    public void Health_DiesOnce_ClampsAndHealsUpToMax()
    {
        var h = new Health(100);
        Assert.False(h.Damage(30));
        h.Heal(50);
        Assert.Equal(100, h.Current);
        Assert.True(h.Damage(150));   // kills
        Assert.Equal(0, h.Current);
        Assert.False(h.Damage(10));   // already dead: no second death
        h.Heal(10);
        Assert.True(h.IsDead);        // dead stays dead
        h.Reset();
        Assert.Equal(100, h.Current);
    }

    [Fact]
    public void Gun_FiresTenThenNeedsReload_ReserveRunsOut()
    {
        var ammo = new GunAmmo();
        for (int i = 0; i < 10; i++) Assert.True(ammo.TryFire());
        Assert.False(ammo.TryFire());           // "Out of ammo"
        Assert.True(ammo.Reload());
        Assert.Equal((10, 20), (ammo.magazineRounds, ammo.bulletCount));
        ammo.TryFire(); ammo.TryFire();
        ammo.Reload();
        Assert.Equal((10, 18), (ammo.magazineRounds, ammo.bulletCount));
        ammo.bulletCount = 1; ammo.magazineRounds = 0;
        ammo.Reload();                           // partial reload with the last bullet
        Assert.Equal((1, 0), (ammo.magazineRounds, ammo.bulletCount));
        ammo.TryFire();
        Assert.False(ammo.Reload());             // "There's no ammo left"
    }

    [Fact]
    public void WeaponProfiles_MatchOriginalNumbers()
    {
        Assert.Equal((4f, 20, true), Profile(1515)); // Sword: Stab
        Assert.Equal((4f, 20, true), Profile(8));    // Axe: Tool.Attack
        Assert.Equal(100, CombatRules.ProfileFor(ItemCatalog.Get(1337))!.Damage); // Pistol: Shoot
        Assert.Equal(ItemClass.Magic, CombatRules.ProfileFor(ItemCatalog.Get(567))!.Class);
        Assert.Null(CombatRules.ProfileFor(ItemCatalog.Get(1)));   // a coin isn't a weapon
        Assert.Equal(MagicStyle.Frost, CombatRules.MagicStyleOf(ItemCatalog.Get(567)!));
        Assert.Equal(MagicStyle.Blood, CombatRules.MagicStyleOf(ItemCatalog.Get(666)!));
        var random = new Random(1);
        Assert.All(Enumerable.Range(0, 200), _ => Assert.InRange(CombatRules.RollMagicDamage(random), 0, 49));

        static (float, int, bool) Profile(int id)
        {
            var p = CombatRules.ProfileFor(ItemCatalog.Get(id))!;
            return (p.Range, p.Damage, p.Knockback);
        }
    }
}

public class EnemyBrainTests
{
    private static EnemyBrain.Decision Run(EnemyBrain b, float seconds, float? target, float step = 0.05f)
    {
        EnemyBrain.Decision last = default;
        for (float t = 0; t < seconds - 1e-4f; t += step) last = b.Tick(step, target);
        return last;
    }

    [Fact]
    public void Wanders_UntilItSeesSomeone_ThenChases()
    {
        var b = new EnemyBrain();
        Assert.True(b.Tick(0.1f, null).Wander);
        var d = b.Tick(0.1f, 6f);
        Assert.Equal(EnemyState.Chase, b.State);
        Assert.True(d.MoveToTarget);
        Assert.Equal(EnemyBrain.ChaseSpeed, d.Speed);
    }

    [Fact]
    public void LosingSight_GoesBackToWander()
    {
        var b = new EnemyBrain();
        b.Tick(0.1f, 6f);
        b.Tick(0.1f, null);
        Assert.Equal(EnemyState.Wander, b.State);
    }

    [Fact]
    public void Attack_HitsOnceAtOneSecond_IfStillInReach_ThenReturnsToChase()
    {
        var b = new EnemyBrain();
        b.Tick(0.1f, 6f);          // chase
        b.Tick(0.1f, 1.5f);        // within action distance: attack
        Assert.Equal(EnemyState.Attack, b.State);

        var hits = 0;
        var ticks = 0;
        while (b.State == EnemyState.Attack && ticks++ < 100)
            if (b.Tick(0.05f, 1.5f).DealAttackDamage) hits++;
        Assert.Equal(1, hits);
        Assert.InRange(ticks * 0.05f, 1.95f, 2.1f);    // lasted ~2 s
        Assert.Equal(EnemyState.Chase, b.State);       // back to previousState
    }

    [Fact]
    public void Attack_Misses_IfTargetStepsAway()
    {
        var b = new EnemyBrain();
        b.Tick(0.1f, 6f);
        b.Tick(0.1f, 1.5f);
        var hits = 0;
        for (int i = 0; i < 40; i++) if (b.Tick(0.05f, 3f).DealAttackDamage) hits++;
        Assert.Equal(0, hits); // "EVADED ATTACK"
    }

    [Fact]
    public void Attack_CannotBeInterrupted_ExceptByDeath()
    {
        var b = new EnemyBrain();
        b.Tick(0.1f, 6f);
        b.Tick(0.1f, 1.5f);
        Assert.False(b.SetState(EnemyState.Wander));
        b.Die();
        Assert.Equal(EnemyState.Death, b.State);
        Assert.False(b.SetState(EnemyState.Chase)); // death is final
    }

    [Fact]
    public void Frozen_StopsForTheTime_ThenResumes()
    {
        var b = new EnemyBrain();
        b.Tick(0.1f, null);
        b.Freeze(1f);
        Assert.True(Run(b, 0.5f, 5f).Stop);
        Assert.Equal(EnemyState.Frozen, b.State); // seeing a player doesn't unfreeze
        Run(b, 0.6f, null);
        Assert.NotEqual(EnemyState.Frozen, b.State);
    }

    [Fact]
    public void Knockback_ShortThenWander()
    {
        var b = new EnemyBrain();
        b.Knockback();
        Run(b, 0.3f, null);
        Assert.Equal(EnemyState.Wander, b.State);
    }
}

public class EnemyPlacementAndHardcoreTests
{
    [Fact]
    public void OriginalSize_FiveEnemies_InTheMiddleOfTheMaze()
    {
        var layout = MazeLayout.Generate(5, 5, 400, seed: 3);
        for (int seed = 0; seed < 50; seed++)
        {
            var nodes = DungeonContents.EnemyNodes(layout, seed);
            Assert.Equal(5, nodes.Count);
            Assert.All(nodes, n => Assert.InRange(n, 10, 14)); // Random.Range(10, 25 - 10)
        }
    }

    [Fact]
    public void HardcoreCharacter_IsMarked_AndDeletionCascades()
    {
        using var db = GameDatabase.OpenInMemory();
        var acct = db.CreateAccount("kermit", "h")!;
        var hc = db.CreateCharacter(acct.Id, "Brave", "000000", hardcore: true).Character!;
        var normal = db.CreateCharacter(acct.Id, "Careful", "000000").Character!;
        Assert.True(hc.Hardcore);
        Assert.False(normal.Hardcore);

        db.SaveInventory(hc.Id, [(0, 1, 5)]);
        db.SaveGear(hc.Id, [1515]);
        var run = db.CreateRun(1, 5, 5, 400, hc.Id);
        db.AddRunParticipant(run, hc.Id);

        Assert.True(db.DeleteCharacter(hc.Id));
        Assert.Null(db.GetCharacter(hc.Id));
        Assert.Empty(db.LoadInventory(hc.Id));
        Assert.Equal(0, db.GetRunSummary(run).Participants);
        Assert.Single(db.ListCharacters(acct.Id));
    }

    [Fact]
    public void RealSchema_V3Data_SurvivesUpgradeToV4_AsNonHardcore()
    {
        using var temp = new TempDatabase();
        temp.CreateAtVersion(3, conn =>
        {
            Exec(conn, "INSERT INTO accounts (id, username, password_hash, created_at) VALUES (1, 'kermit', 'h', '2026-11-15T00:00:00.0000000Z');");
            Exec(conn, "INSERT INTO characters (id, account_id, name, color, created_at) VALUES (7, 1, 'Kermit', '3cb043', '2026-11-15T00:00:00.0000000Z');");
            Exec(conn, "INSERT INTO equipment_items (character_id, slot, item_id) VALUES (7, 10, 1515);");
        });
        using var db = GameDatabase.Open(temp.Path);
        var c = db.GetCharacter(7)!;
        Assert.False(c.Hardcore);
        Assert.Equal(1515, db.LoadGear(7, Gear.SlotCount)[10]);
    }
}
