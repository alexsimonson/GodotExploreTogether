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

    private static readonly ItemDef Pistol = ItemCatalog.Get(1337)!;

    [Fact]
    public void Gun_StartsLoaded_FiresTen_ThenReloadsFromCarriedRounds()
    {
        var inv = new Inventory();
        var gear = new Gear();
        inv.AddItem(1337);
        Assert.Equal(10, inv.slots[0].data);                         // a fresh pistol comes loaded
        SlotContainerLogic.Move(inv, gear, ItemContainer.Inventory, 0, ItemContainer.Gear, (int)EquipmentType.Weapon);
        var weapon = gear.WeaponSlot;
        for (int i = 0; i < 10; i++) Assert.True(GunRules.TryFire(weapon));
        Assert.False(GunRules.TryFire(weapon));                      // "Out of ammo"

        Assert.Equal(0, GunRules.Reload(weapon, Pistol, inv));       // no rounds carried
        inv.AddItems(ItemCatalog.PistolRoundsId, 25);
        Assert.Equal(10, GunRules.Reload(weapon, Pistol, inv));
        Assert.Equal((10, 15), (GunRules.Loaded(weapon), GunRules.Reserve(inv, Pistol)));
        GunRules.TryFire(weapon); GunRules.TryFire(weapon);
        Assert.Equal(2, GunRules.Reload(weapon, Pistol, inv));        // tops up only what's missing
        Assert.Equal(13, GunRules.Reserve(inv, Pistol));
        Assert.Equal(0, GunRules.Reload(weapon, Pistol, inv));        // already full
    }

    [Fact]
    public void Gun_PartialReload_UsesTheLastRounds_AndEmptiesTheStack()
    {
        var inv = new Inventory();
        inv.AddItems(ItemCatalog.PistolRoundsId, 3);
        var weapon = new ItemSlot { itemId = 1337, stack_size = 1, data = 0 };
        Assert.Equal(3, GunRules.Reload(weapon, Pistol, inv));
        Assert.Equal(3, GunRules.Loaded(weapon));
        Assert.False(inv.Contains(ItemCatalog.PistolRoundsId));
    }

    [Fact]
    public void LoadedRounds_TravelWithTheGun_ThroughMovesAndDrops()
    {
        var inv = new Inventory();
        var gear = new Gear();
        inv.AddItem(1337, data: 4);                                   // e.g. a pistol someone dropped with 4 rounds
        SlotContainerLogic.Move(inv, gear, ItemContainer.Inventory, 0, ItemContainer.Gear, (int)EquipmentType.Weapon);
        Assert.Equal(4, gear.WeaponSlot.data);
        SlotContainerLogic.Move(inv, gear, ItemContainer.Gear, (int)EquipmentType.Weapon, ItemContainer.Inventory, 6);
        Assert.Equal(4, inv.slots[6].data);
        Assert.Equal((1337, 1, 4, (long?)null), SlotContainerLogic.Drop(inv, gear, ItemContainer.Inventory, 6));
        Assert.Null(inv.slots[6].data);

        var other = new Inventory();                                  // another player picks it up
        other.AddItems(1337, 1, data: 4);
        Assert.Equal(4, other.slots[0].data);
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
    public void GunState_IsSaved_ForInventoryAndGear()
    {
        using var db = GameDatabase.OpenInMemory();
        var acct = db.CreateAccount("kermit", "h")!;
        var c = db.CreateCharacter(acct.Id, "Kermit", "000000").Character!.Id;
        db.SaveInventory(c, [(0, 1337, 1, (int?)3), (1, ItemCatalog.PistolRoundsId, 20, (int?)null)]);
        var gearIds = Enumerable.Repeat(-1, Gear.SlotCount).ToArray();
        var gearData = Enumerable.Repeat(-1, Gear.SlotCount).ToArray();
        gearIds[(int)EquipmentType.Weapon] = 1337;
        gearData[(int)EquipmentType.Weapon] = 7;
        db.SaveGear(c, gearIds, gearData);

        Assert.Equal([new SlotRecord(0, 1337, 1, 3), new SlotRecord(1, ItemCatalog.PistolRoundsId, 20)], db.LoadInventory(c));
        var (ids, data) = db.LoadGearWithData(c, Gear.SlotCount);
        var gear = new Gear();
        gear.ImportGear(ids, data);
        Assert.Equal(7, gear.WeaponSlot.data);
    }

    [Fact]
    public void RealSchema_V4Data_SurvivesUpgradeToV5_GunsLoadFull()
    {
        using var temp = new TempDatabase();
        temp.CreateAtVersion(4, conn =>
        {
            Exec(conn, "INSERT INTO accounts (id, username, password_hash, created_at) VALUES (1, 'kermit', 'h', '2026-11-15T00:00:00.0000000Z');");
            Exec(conn, "INSERT INTO characters (id, account_id, name, color, created_at) VALUES (7, 1, 'Kermit', '3cb043', '2026-11-15T00:00:00.0000000Z');");
            Exec(conn, "INSERT INTO inventory_items (character_id, slot, item_id, count) VALUES (7, 0, 1337, 1);");
        });
        using var db = GameDatabase.Open(temp.Path);
        var inv = new Inventory();
        inv.Load(db.LoadInventory(7));
        Assert.Equal(10, inv.slots[0].data); // a pistol saved before guns had state comes back loaded
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
