using ExploreTogether.Core.City;
using ExploreTogether.Core.Items;
using ExploreTogether.Core.Zombies;

namespace ExploreTogether.Core.Tests;

public class ZombieRulesTests
{
    [Fact]
    public void Headshots_DestroyZombies_BodyShotsBarelySlowThem()
    {
        var pistol = ItemCatalog.Get(1337)!;
        Assert.True(ZombieRules.Hit(pistol, 10, head: true).Destroyed);
        var body = ZombieRules.Hit(pistol, 10, head: false);
        Assert.False(body.Destroyed);
        Assert.True(body.Damage < 10);
        Assert.True(ZombieRules.Hit(ItemCatalog.Get(ItemCatalog.PumpShotgunId), 10, false).Damage > 10); // slugs hit hard
    }

    [Fact]
    public void BluntWeaponsKnockDown_BladesCut()
    {
        Assert.True(ZombieRules.Hit(ItemCatalog.Get(ItemCatalog.BatId), 10, false).KnockdownSeconds > 0);
        Assert.True(ZombieRules.Hit(ItemCatalog.Get(ItemCatalog.ChairId), 15, false).KnockdownSeconds > 0);
        var sword = ZombieRules.Hit(ItemCatalog.Get(1515), 10, false);
        Assert.Equal(0, sword.KnockdownSeconds);
        Assert.True(sword.Damage > 20);
    }

    [Fact]
    public void Flashlights_ShowYouFromAfar_AtNight()
    {
        Assert.True(ZombieRules.SightRange(0, true) > ZombieRules.SightRange(0, false) * 2);
        Assert.Equal(ZombieRules.SightRange(1, false), ZombieRules.SightRange(1, true)); // by day it makes no difference
    }
}

public class ZombieBrainTests
{
    /// <summary>An open field with one door at (10, 0): every route goes straight, through the door when crossing x = 10.</summary>
    private sealed class Field : IZombieWorld
    {
        public bool DoorShut;
        public bool Walls;
        public IReadOnlyList<ZombieStep> Route(float fx, float fz, float fy, float tx, float tz, float ty) =>
            fx < 10 != tx < 10 ? [new ZombieStep(9.5f, 0), new ZombieStep(10.5f, 0, 0, Door: 1), new ZombieStep(tx, tz)] : [new ZombieStep(tx, tz)];
        public (float X, float Z) WanderPoint(float x, float z, Random random) => (x + 2, z);
        public bool CanSee(float ax, float az, float ay, float bx, float bz, float by) => !Walls;
        public bool DoorClosed(int door) => DoorShut;
    }

    private static void Run(ZombieBrain z, Field world, List<Prey> prey, float seconds, float daylight = 1)
    {
        for (var t = 0f; t < seconds; t += 0.1f)
        {
            if ((int)(t * 10) % 2 == 0) z.Sense(prey, [], daylight, world);
            z.Update(0.1f, world, prey);
        }
    }

    [Fact]
    public void SeesSomeone_ChasesThem_AndBites()
    {
        var world = new Field();
        var z = new ZombieBrain(1, 0, 0, 1);
        var prey = new List<Prey> { new(7, 6, 0) };
        Run(z, world, prey, 6);
        Assert.Equal(7, z.Target);
        var bites = new List<ZombieIntent>();
        while (z.TryTakeIntent(out var i)) bites.Add(i);
        Assert.Contains(bites, i => i.Kind == ZombieIntentKind.Bite && i.Target == 7);
    }

    [Fact]
    public void DoesntSeeThroughWalls_ButHearsGunfire()
    {
        var world = new Field { Walls = true };
        var z = new ZombieBrain(1, 0, 0, 1);
        Run(z, world, [new(7, 6, 0)], 2);
        Assert.Equal(0, z.Target);
        z.Sense([], [new Heard(30, 0, 55)], 1, world);
        for (var i = 0; i < 100; i++) z.Update(0.1f, world, []);
        Assert.True(z.X > 5, $"went toward the shot ({z.X:0.0})");
    }

    [Fact]
    public void AShutDoor_IsBattered_NotWalkedThrough()
    {
        var world = new Field { DoorShut = true };
        var z = new ZombieBrain(1, 0, 0, 1);
        var prey = new List<Prey> { new(7, 14, 0) };
        Run(z, world, prey, 10);
        Assert.True(z.X is > 9 and <= 9.51f, $"stopped at the door ({z.X:0.00})");
        Assert.Equal(ZombieState.Bash, z.State);
        var bashes = 0;
        while (z.TryTakeIntent(out var i)) if (i.Kind == ZombieIntentKind.Bash && i.Door == 1) bashes++;
        Assert.True(bashes >= 2);
        world.DoorShut = false; // it gave way
        Run(z, world, prey, 6);
        Assert.True(z.X > 12);
    }

    [Fact]
    public void KnockedDown_ItGetsUp_Headshot_ItStaysDown()
    {
        var world = new Field();
        var z = new ZombieBrain(1, 0, 0, 1);
        z.TakeHit(ZombieRules.Hit(ItemCatalog.Get(ItemCatalog.BatId), 10, false), 1, 0);
        Assert.Equal(ZombieState.Down, z.State);
        Run(z, world, [], ZombieRules.KnockdownSeconds + 0.5f);
        Assert.NotEqual(ZombieState.Down, z.State);
        Assert.True(z.TakeHit(ZombieRules.Hit(ItemCatalog.Get(1337), 10, true), 1, 0));
        Assert.Equal(ZombieState.Dead, z.State);
    }

    [Fact]
    public void InACity_ZombiesFindTheirWayAlongTheStreets()
    {
        var city = CityLayout.Generate(5, 5, 2);
        var paths = new CityPaths(city);
        var world = new CityZombieWorld(city, paths, new CityDoors(city));
        var (sx, sz) = CityZombieWorld.SpawnPoint(paths, new Random(1), 0, 0, 40);
        var z = new ZombieBrain(1, sx, sz, 3);
        var (tx, tz) = CityLayout.CellCenter(paths.StreetCells[0]);
        var prey = new List<Prey> { new(5, tx, tz) };
        // No sight test in this world: it sees them from anywhere within range once close; head over by noise first.
        z.Sense([], [new Heard(tx, tz, 200)], 1, world);
        for (var i = 0; i < 6000 && Math.Abs(z.X - tx) + Math.Abs(z.Z - tz) > 3; i++)
        {
            if (i % 2 == 0) z.Sense(prey, [], 1, world);
            z.Update(0.1f, world, prey);
        }
        var route = world.Route(sx, sz, 0, tx, tz, 0);
        Assert.True(Math.Abs(z.X - tx) + Math.Abs(z.Z - tz) <= 3, $"route {route.Count} state {z.State} from ({sx:0},{sz:0}) got to ({z.X:0}, {z.Z:0}), wanted ({tx:0}, {tz:0})");
    }
}
