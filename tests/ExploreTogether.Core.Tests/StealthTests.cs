using ExploreTogether.Core.City;
using ExploreTogether.Core.Combat;
using ExploreTogether.Core.Dungeon;
using ExploreTogether.Core.Items;
using ExploreTogether.Core.Net;

namespace ExploreTogether.Core.Tests;

public class MoveGuardTests
{
    [Fact]
    public void WalkingAndSprinting_AreAccepted()
    {
        var g = new MoveGuard(0, 0, 0, 0);
        for (var i = 1; i <= 120; i++)
            Assert.True(g.Check(i * PlayerSpeeds.Sprint / 60f, 0, 0, i / 60.0, PlayerSpeeds.Sprint));
        Assert.Equal(16, g.X, 2);
    }

    [Fact]
    public void Teleports_AreRejected_AndChangeNothing()
    {
        var g = new MoveGuard(0, 0, 0, 0);
        Assert.False(g.Check(30, 0, 0, 0.1, PlayerSpeeds.Sprint));
        Assert.Equal(0, g.X);
        Assert.True(g.Check(0.5f, 0, 0, 0.2, PlayerSpeeds.Sprint));
    }

    [Fact]
    public void BunchedPackets_AfterAHiccup_AreCovered()
    {
        var g = new MoveGuard(0, 0, 0, 0);
        // Nothing for a second, then a second's worth of walking arrives at once.
        for (var i = 1; i <= 10; i++)
            Assert.True(g.Check(i * 0.5f, 0, 0, 1.0 + i * 0.001, PlayerSpeeds.Walk));
    }

    [Fact]
    public void HeldPlayers_CanBarelyMove()
    {
        var g = new MoveGuard(0, 0, 0, 0);
        var x = 0f;
        var rejected = 0;
        for (var i = 1; i <= 120; i++)
        {
            x += PlayerSpeeds.Walk / 60f;
            if (!g.Check(x, 0, 0, i / 60.0, PlayerSpeeds.Held)) { rejected++; x = g.X; }
        }
        Assert.True(g.X < PlayerSpeeds.Held * 2 + MoveGuard.Slack + 0.1f, $"moved {g.X} m in 2 s while held");
        Assert.True(rejected > 0);
    }

    [Fact]
    public void Falling_IsFine_FlyingUpIsNot()
    {
        var g = new MoveGuard(0, 10, 0, 0);
        Assert.True(g.Check(0, 0, 0, 0.5, PlayerSpeeds.Walk));
        Assert.False(g.Check(0, 8, 0, 0.6, PlayerSpeeds.Walk));
    }
}

public class BreachingTests
{
    private static CityLayout City() => CityLayout.Generate(5, 5, 4);

    [Fact]
    public void Kicks_BreakALockedDoor_AfterItsStrength_AndAnUnlockedOneAtOnce()
    {
        var city = City();
        var doors = new CityDoors(city);
        doors.Lock(0);
        var strength = Breaching.KicksToBreak(city.Parcels[0].Building);
        for (var i = 1; i < strength; i++) Assert.False(doors.Kick(0, strength));
        Assert.True(doors.Kick(0, strength));
        Assert.True(doors.IsBroken(0) && doors.IsOpen(0) && !doors.IsLocked(0));
        // Broken: it won't close or lock, and a kick does nothing more.
        doors.Close(0);
        Assert.True(doors.IsOpen(0));
        Assert.False(doors.Lock(0));
        Assert.False(doors.Kick(0, strength));

        Assert.True(doors.Kick(1, 6)); // unlocked: bursts open on the first kick
    }

    [Fact]
    public void AShotOutLock_LeavesTheDoorUsable_ButNeverLockable()
    {
        var doors = new CityDoors(City());
        doors.Lock(2);
        Assert.True(doors.BreakLock(2));
        Assert.False(doors.IsLocked(2));
        Assert.True(doors[2].LockBroken && !doors.IsBroken(2));
        Assert.True(doors.Open(2));
        doors.Close(2);
        Assert.False(doors.IsOpen(2));
        Assert.False(doors.Lock(2));
        Assert.False(doors.BreakLock(2));
    }

    [Fact]
    public void DoorFlags_RoundTrip()
    {
        var s = new CityDoors.DoorState(true, false, true, true, true);
        Assert.Equal(s, CityDoors.DoorState.FromFlags(s.Flags));
        Assert.Equal(0, default(CityDoors.DoorState).Flags);
    }

    [Fact]
    public void Picking_TakesLongerOnBetterLocks_AndSucceedsAboutSevenInTen()
    {
        Assert.True(Breaching.PickSeconds(BuildingType.Guardhouse) > Breaching.PickSeconds(BuildingType.Cottage));
        Assert.InRange(Breaching.PickSeconds(BuildingType.Manor), 4, 8);
        var random = new Random(3);
        var wins = Enumerable.Range(0, 2000).Count(_ => Breaching.RollPick(random));
        Assert.InRange(wins, 1300, 1500);
    }

    [Fact]
    public void Grappling_ToughPeopleEscapeMore_AndSurpriseHalvesIt()
    {
        Assert.True(Breaching.EscapeChancePerSecond(1, false) > Breaching.EscapeChancePerSecond(0.2f, false));
        Assert.Equal(Breaching.EscapeChancePerSecond(0.6f, false) / 2, Breaching.EscapeChancePerSecond(0.6f, true), 4);
        Assert.True(Breaching.StrugglePerPress(0.1f) > Breaching.StrugglePerPress(0.9f));
        Assert.InRange(1 / Breaching.StrugglePerPress(1), 14, 16); // presses against the strongest
    }

    [Fact]
    public void FromBehind_IsOutsideTheirView()
    {
        // Facing -Z (yaw 0): someone at +Z is behind, someone at -Z in front.
        Assert.True(Breaching.FromBehind(0, 0, 0, 0, 1));
        Assert.False(Breaching.FromBehind(0, 0, 0, 0, -1));
        Assert.False(Breaching.FromBehind(0, 0, 0, 1, -0.2f)); // to the side, a little ahead
    }

    [Fact]
    public void Shotguns_TakeSlugs_AndBreach_PumpIsSlower()
    {
        var pump = ItemCatalog.Get(ItemCatalog.PumpShotgunId)!;
        var dbl = ItemCatalog.Get(ItemCatalog.DoubleBarrelId)!;
        Assert.True(pump.Breaches && dbl.Breaches);
        Assert.Equal(ItemCatalog.SlugId, pump.AmmoItemId);
        Assert.Equal(2, dbl.MagazineSize);
        Assert.Equal(6, pump.MagazineSize);
        Assert.True(CombatRules.ProfileFor(pump)!.Cooldown > CombatRules.ProfileFor(dbl)!.Cooldown);
        Assert.Equal(40, CombatRules.ProfileFor(pump)!.Range);
        Assert.False(ItemCatalog.Get(1337)!.Breaches);
        Assert.True(ItemCatalog.Get(ItemCatalog.LockpickId)!.Stack);
    }
}

public class CityLightingAndAlleyTests
{
    [Fact]
    public void Night_IsDark_ExceptUnderLamps_AndDayIsBright()
    {
        var city = CityLayout.Generate(5, 5, 4);
        var light = new CityLighting(city);
        Assert.NotEmpty(light.Lamps);
        var (lx, lz) = light.Lamps[0];
        Assert.True(light.LightAt(lx, lz, 1) > 0.6f, "under a lamp at night");
        Assert.True(light.LightAt(lx, lz, 12) > 0.95f, "daylight");
        // Somewhere on a street far from every lamp is dark at night.
        var paths = new CityPaths(city);
        var dark = paths.StreetCells.Select(CityLayout.CellCenter)
            .Where(c => light.Lamps.All(l => MathF.Sqrt((l.X - c.X) * (l.X - c.X) + (l.Z - c.Z) * (l.Z - c.Z)) > CityLighting.LampRadius));
        Assert.All(dark, c => Assert.True(light.LightAt(c.X, c.Z, 1) < 0.15f));
        Assert.False(CityLighting.LampsOn(12));
        Assert.True(CityLighting.LampsOn(22));
    }

    [Fact]
    public void LitBuildings_AreBrightInside_AndDarkWhenNot()
    {
        var city = CityLayout.Generate(5, 5, 4);
        var light = new CityLighting(city);
        var p = city.Parcels[0];
        var (x, z) = p.Spots[0];
        Assert.True(light.LightAt(x, z, 23, _ => true) > 0.7f);
        Assert.True(light.LightAt(x, z, 23, _ => false) < 0.1f);
    }

    [Fact]
    public void Alleys_RunBetweenCommercialLots_OpenOntoAStreet_AndBelongToNobody()
    {
        var withAlleys = 0;
        for (var seed = 0; seed < 6; seed++)
        {
            var city = CityLayout.Generate(6, 6, seed);
            var light = new CityLighting(city);
            foreach (var a in city.Alleys)
            {
                withAlleys++;
                var r = a.Rect;
                Assert.Equal(CityLayout.AlleyWidth, MathF.Min(r.Width, r.Depth), 3);
                Assert.Null(city.ParcelAt(r.CenterX, r.CenterZ));
                Assert.Same(a, city.AlleyAt(r.CenterX, r.CenterZ));
                Assert.NotEmpty(a.Mouths);
                Assert.All(a.Mouths, m => Assert.True(city.IsStreet(CityLayout.CellAt(m.X, m.Z)), "an alley's mouth is on a street"));
                Assert.True(light.LightAt(r.CenterX, r.CenterZ, 23) < 0.1f, "alleys are dark at night");
            }
            foreach (var p in city.Parcels.Where(p => p.AlleySides != 0))
            {
                Assert.Equal(Zone.Commercial, p.Zone);
                Assert.True(p.Footprint.MinX >= p.Bounds.MinX && p.Footprint.MaxX <= p.Bounds.MaxX
                            && p.Footprint.MinZ >= p.Bounds.MinZ && p.Footprint.MaxZ <= p.Bounds.MaxZ, "the building fits the narrower lot");
            }
        }
        Assert.True(withAlleys > 3, $"only {withAlleys} alleys in 6 cities");
    }
}

public class StealthRulesTests
{
    private static readonly CityLayout City = CityLayout.Generate(5, 5, 4);
    private static Person Owner(NpcRole role = NpcRole.Civilian, NpcTraits traits = NpcTraits.None) =>
        new(1, "Owner", role, 3, -1) { Traits = traits };
    private static PlayerSighting Seen(Conduct c, int parcel = 3, float clarity = 1) => new(7, "P", 0, 0, c, parcel, clarity);

    [Fact]
    public void SightRange_GrowsWithLight_AndShrinksCrouched()
    {
        Assert.True(Stealth.SightRange(1, false, false) >= 35);
        Assert.True(Stealth.SightRange(CityLighting.Moonlight, false, false) < 8);
        Assert.True(Stealth.SightRange(0.5f, true, false) < Stealth.SightRange(0.5f, false, false));
        Assert.True(Stealth.SightRange(0, true, false) < 2.5f);
    }

    [Fact]
    public void ViewCone_IsAhead_AndWiderWhenAlert()
    {
        Assert.True(Stealth.InView(0, 0, 0, 0, -10, false));
        Assert.False(Stealth.InView(0, 0, 0, 0, 10, false));
        Assert.False(Stealth.InView(0, 0, 0, 10, -3, false)); // off to the side
        Assert.True(Stealth.InView(0, 0, 0, 10, -3, true));
        Assert.True(Stealth.InView(0, 0, 0, 0, 1, false)); // right behind, but touching distance
    }

    [Fact]
    public void Trespass_MattersMostToOwners_AndInsideIsWorse()
    {
        var (yard, _, _) = Stealth.Judge(Owner(), Seen(Conduct.TrespassYard), City, 12);
        var (inside, sev, _) = Stealth.Judge(Owner(), Seen(Conduct.TrespassInside), City, 12);
        var (bystander, _, _) = Stealth.Judge(new Person(2, "B", NpcRole.Civilian, 9, -1), Seen(Conduct.TrespassInside), City, 12);
        Assert.True(inside > yard && yard > bystander);
        Assert.Equal(Severity.Crime, sev);
        var (police, _, _) = Stealth.Judge(new Person(3, "Cop", NpcRole.Police, 5, 5), Seen(Conduct.TrespassInside), City, 12);
        Assert.Equal(inside, police); // police have authority everywhere
    }

    [Fact]
    public void Violence_AndDragging_AlarmAtOnce()
    {
        Assert.True(float.IsPositiveInfinity(Stealth.Judge(Owner(), Seen(Conduct.Violent, -1), City, 12).Rate));
        Assert.Equal(Severity.Violent, Stealth.Judge(Owner(), Seen(Conduct.Violent, -1), City, 12).Severity);
        Assert.True(float.IsPositiveInfinity(Stealth.Judge(Owner(), Seen(Conduct.Dragging, -1), City, 12).Rate));
    }

    [Fact]
    public void AGunInPublic_WorriesEveryone_PoliceMost()
    {
        var civ = Stealth.Judge(new Person(2, "B", NpcRole.Civilian, 9, -1), Seen(Conduct.GunOut, -1), City, 12).Rate;
        var cop = Stealth.Judge(new Person(3, "C", NpcRole.Police, 5, 5), Seen(Conduct.GunOut, -1), City, 12).Rate;
        Assert.True(civ > 0 && cop > civ);
        Assert.Equal(0, Stealth.Judge(new Person(2, "B", NpcRole.Civilian, 9, -1), Seen(Conduct.None, -1), City, 12).Rate);
    }

    [Fact]
    public void Noises_CarryHalfAsFarThroughWalls_AndWakeSleepers()
    {
        var n = new Noise(0, 0, Stealth.KickRadius, "door kicked", Severity.Crime);
        Assert.True(Stealth.Hears(n, 20, 0, false));
        Assert.False(Stealth.Hears(n, 20, 0, true));
        Assert.True(Stealth.Wakes(n, Owner()));
        Assert.False(Stealth.Wakes(n, Owner(traits: NpcTraits.HeavySleeper)));
        Assert.True(Stealth.Wakes(n with { Radius = Stealth.GunshotRadius }, Owner(traits: NpcTraits.HeavySleeper)));
    }

    [Fact]
    public void Traits_VaryByRole()
    {
        var people = Enumerable.Range(0, 400).Select(i => CityPopulation.WithTraits(new Person(i, "X", (NpcRole)(i % 5), 0, 0), 9)).ToList();
        Assert.All(people.Where(p => p.Role is NpcRole.Police or NpcRole.Security), p => Assert.False(p.Has(NpcTraits.Timid)));
        Assert.All(people.Where(p => p.Role is NpcRole.Police or NpcRole.Security), p => Assert.True(p.Has(NpcTraits.Armed)));
        Assert.Contains(people, p => p.Role == NpcRole.Civilian && p.Has(NpcTraits.Timid));
        Assert.Contains(people, p => p.Role == NpcRole.Civilian && p.Has(NpcTraits.Brave));
        Assert.All(people, p => Assert.InRange(p.Grit, 0.1f, 1f));
    }
}

/// <summary>Residents' reactions, run through their brains without the server (perception fed by hand).</summary>
public class ReactionTests
{
    private const float Tick = 0.05f;

    private sealed class World
    {
        public required CityLayout City;
        public required CityPopulation Population;
        public required List<CityNpcBrain> Brains;
        public required CityClock Clock;
        public required CityCrowd Crowd;
        public required CityDoors Doors;
        public readonly List<NpcIntent> Intents = new();

        public void Run(double seconds, Func<CityNpcBrain, Percepts?>? perceive = null)
        {
            var percepts = new Percepts();
            for (var t = 0.0; t < seconds; t += Tick)
            {
                Clock.Advance(Tick);
                Crowd.Step(Tick, Clock.Time);
                if (perceive != null && (int)(t / 0.2) != (int)((t + Tick) / 0.2))
                    foreach (var b in Brains)
                        if (perceive(b) is { } p) b.Perceive(p, 0.2f, Clock.HourOfDay);
                foreach (var b in Brains)
                    while (b.TryTakeIntent(out var i)) Intents.Add(i);
            }
        }
    }

    private static World Start(int seed = 4, float hour = 10)
    {
        var city = CityLayout.Generate(5, 5, seed);
        var pop = CityPopulation.Generate(city, seed);
        var paths = new CityPaths(city);
        var clock = new CityClock(hour, 1 / 120f);
        var brains = pop.People.Select(p => new CityNpcBrain(p, city, paths, seed, clock.Time)).ToList();
        var crowd = new CityCrowd();
        foreach (var b in brains) crowd.Add(b);
        var doors = new CityDoors(city);
        crowd.SetUpDoors(city, doors, clock.Time);
        return new World { City = city, Population = pop, Brains = brains, Clock = clock, Crowd = crowd, Doors = doors };
    }

    private static Percepts SeeingPlayer(CityNpcBrain b, float x, float z, Conduct c, int parcel)
    {
        var p = new Percepts();
        p.Players.Add(new PlayerSighting(42, "Intruder", x, z, c, parcel, 1));
        return p;
    }

    [Fact]
    public void AnOwner_ConfrontsATrespasserInTheirHome_ThenEscalatesIfIgnored()
    {
        var w = Start(hour: 20);
        w.Run(1);
        var owner = w.Brains.First(b => b.Inside == b.Person.Home && b.Person.Role is NpcRole.Civilian or NpcRole.Worker && !b.Person.Has(NpcTraits.Timid));
        var home = w.City.Parcels[owner.Person.Home];
        var (x, z) = home.Spots[0];
        w.Run(3, b => b == owner ? SeeingPlayer(b, x, z, Conduct.TrespassInside, home.Id) : null);
        Assert.Equal(42, owner.Target);
        Assert.True(owner.Mood >= Mood.Suspicious, $"mood {owner.Mood}");
        Assert.NotEqual("", owner.Speech);
        // Ignored for long enough: alarmed (detain, flee or call), and the police hear of it.
        w.Run(25, b => b == owner ? SeeingPlayer(b, x, z, Conduct.TrespassInside, home.Id) : null);
        Assert.True(owner.Mood >= Mood.Alarmed, $"mood {owner.Mood} ({owner.Activity})");
        Assert.Contains(w.Intents, i => i.Kind == NpcIntentKind.Report && i.Player == 42);
    }

    [Fact]
    public void Gunfire_SendsTimidPeopleRunning_AndPoliceOnDutyToIt()
    {
        var w = Start(hour: 10);
        w.Run(1);
        var cop = w.Brains.First(b => b.Person.Role == NpcRole.Police && b.Activity == "patrolling");
        var timid = w.Brains.Where(b => b.Person.Has(NpcTraits.Timid) && b.Person.Role != NpcRole.Police && b.Inside < 0).ToList();
        var shot = new Noise(cop.X + 10, cop.Z, Stealth.GunshotRadius, "gunshot", Severity.Violent, 42);
        Percepts Hear(CityNpcBrain b)
        {
            var p = new Percepts();
            if (Stealth.Hears(shot, b.X, b.Z, false)) p.Noises.Add(shot);
            return p;
        }
        w.Brains.ForEach(b => b.Perceive(Hear(b), 0.2f, 10));
        Assert.Equal("responding", cop.Activity);
        foreach (var t in timid.Where(t => Stealth.Hears(shot, t.X, t.Z, false)))
            Assert.Equal("fleeing", t.Activity);
    }

    [Fact]
    public void Police_ArrestThoseTheySeeCommittingACrime_AndGrabThemUpClose()
    {
        var w = Start(hour: 10);
        w.Run(1);
        var cop = w.Brains.First(b => b.Person.Role == NpcRole.Police && b.Activity == "patrolling");
        // The player stands still, a few meters ahead of the officer, dragging a body.
        var (px, pz) = (cop.X - MathF.Sin(cop.Yaw) * 4, cop.Z - MathF.Cos(cop.Yaw) * 4);
        w.Run(4, b => b == cop ? SeeingPlayer(b, px, pz, Conduct.Dragging, -1) : null);
        Assert.Equal("arresting", cop.Activity);
        Assert.Contains(w.Intents, i => i.Kind == NpcIntentKind.Grab && i.Player == 42);
        cop.GrabResult(42, true);
        w.Run(Breaching.ArrestSeconds + 0.5, b => b == cop ? SeeingPlayer(b, px, pz, Conduct.None, -1) : null);
        Assert.Contains(w.Intents, i => i.Kind == NpcIntentKind.Arrest && i.Player == 42);
    }

    [Fact]
    public void BreakingFree_IsResisting_AndPoliceUseForce()
    {
        var w = Start(hour: 10);
        w.Run(1);
        var cop = w.Brains.First(b => b.Person.Role == NpcRole.Police && b.Activity == "patrolling");
        var (px, pz) = (cop.X - MathF.Sin(cop.Yaw) * 1.2f, cop.Z - MathF.Cos(cop.Yaw) * 1.2f);
        w.Run(1, b => b == cop ? SeeingPlayer(b, px, pz, Conduct.Dragging, -1) : null);
        cop.GrabResult(42, true);
        cop.GripBroken(42);
        Assert.Equal(Mood.Hostile, cop.Mood);
        Assert.True(cop.WeaponOut);
    }

    [Fact]
    public void AResidentHeldByAPlayer_StaysPut_AndRemembersThemWhenFree()
    {
        var w = Start(hour: 10);
        w.Run(1);
        var victim = w.Brains.First(b => b.Person.Role == NpcRole.Civilian && b.Inside < 0);
        victim.GrabbedBy(42, victim.X, victim.Z + 0.7f, surprised: true);
        var (x, z) = (victim.X, victim.Z);
        w.Run(2);
        Assert.Equal((x, z), (victim.X, victim.Z));
        Assert.True(victim.Held);
        Assert.DoesNotContain(w.Intents, i => i.Kind == NpcIntentKind.Noise); // grabbed by surprise: no scream
        victim.Released(42, escaped: true);
        Assert.False(victim.Held);
        Assert.True(victim.Mood >= Mood.Alarmed, $"{victim.Mood} ({victim.Activity})");
        Assert.True(victim.SuspicionOf(42).Level >= Stealth.Alarmed);
    }

    [Fact]
    public void ADeadBody_MakesPeopleScream_AndCallThePolice()
    {
        var w = Start(hour: 10);
        w.Run(1);
        var walker = w.Brains.First(b => b.Person.Role == NpcRole.Civilian && b.Inside < 0);
        var body = new BodySighting(999, walker.X + 3, walker.Z, Dead: true);
        walker.Perceive(new Percepts { Bodies = { body } }, 0.2f, 10);
        w.Run(0.1);
        Assert.Contains(w.Intents, i => i.Kind == NpcIntentKind.Report && i.Severity == Severity.Violent);
        Assert.Contains(w.Intents, i => i.Kind == NpcIntentKind.Noise && i.Text == "scream");
        Assert.Equal("fleeing", walker.Activity);
    }

    [Fact]
    public void ADispatchedOfficer_RecognizesTheSuspectOnSight()
    {
        var w = Start(hour: 10);
        w.Run(1);
        var cop = w.Brains.First(b => b.Person.Role == NpcRole.Police && b.Activity == "patrolling");
        Assert.True(cop.Dispatch(cop.X + 30, cop.Z, 42, Severity.Crime));
        Assert.Equal("responding", cop.Activity);
        // Seen doing nothing wrong, the description is enough.
        var (px, pz) = (cop.X - MathF.Sin(cop.Yaw) * 6, cop.Z - MathF.Cos(cop.Yaw) * 6);
        cop.Perceive(SeeingPlayer(cop, px, pz, Conduct.None, -1), 0.2f, 10);
        Assert.Equal("arresting", cop.Activity);
    }

    [Fact]
    public void AfterTheTrouble_TheyGoBackToTheirDay()
    {
        var w = Start(hour: 10);
        w.Run(1);
        var walker = w.Brains.First(b => b.Person.Role == NpcRole.Civilian && b.Inside < 0 && !b.Person.Has(NpcTraits.Timid));
        var (px, pz) = (walker.X - MathF.Sin(walker.Yaw) * 6, walker.Z - MathF.Cos(walker.Yaw) * 6);
        w.Run(5, b => b == walker ? SeeingPlayer(b, px, pz, Conduct.GunOut, -1) : null);
        Assert.True(walker.Mood >= Mood.Curious);
        w.Run(60, b => b == walker ? new Percepts() : null); // the player is gone
        Assert.Equal(Mood.Calm, walker.Mood);
        Assert.Equal(0, walker.Target);
    }

    [Fact]
    public void Routes_ReachAnywhere_ThroughDoorsAndGates()
    {
        var city = CityLayout.Generate(5, 5, 4);
        var paths = new CityPaths(city);
        var a = city.Parcels[0];
        var b = city.Parcels[^1];
        var route = CityNav.Route(city, paths, a.Spots[0], b.Spots[0], (0, 0));
        Assert.Equal(b.Spots[0], (route[^1].X, route[^1].Z));
        Assert.Contains(route, p => p.Door == a.Id && !p.Entering); // out through their own door
        Assert.Contains(route, p => p.Door == b.Id && p.Entering);  // in through the other's
        // Within one parcel, from the yard to inside: through its door.
        var way = CityPaths.WayIn(a, a.Spots[0]);
        var inner = CityNav.Route(city, paths, way[1], a.Spots[0], (0, 0));
        Assert.Contains(inner, p => p.Door == a.Id && p.Entering);
    }
}
