using ExploreTogether.Core.City;

namespace ExploreTogether.Core.Tests;

public class CityPopulationTests
{
    [Theory]
    [InlineData(5, 1)]
    [InlineData(5, 2)]
    [InlineData(10, 3)]
    public void Population_HasEveryRole_AndValidPlaces(int size, int seed)
    {
        var city = CityLayout.Generate(size, size, seed);
        var pop = CityPopulation.Generate(city, seed);
        Assert.Equal(CityPopulation.DefaultCount(city), pop.People.Count);
        Assert.Equal(city.Parcels.Count(p => p.Building == BuildingType.Guardhouse) * CityPopulation.PolicePerStation,
            pop.People.Count(p => p.Role == NpcRole.Police));
        Assert.Contains(pop.People, p => p.Role == NpcRole.Worker);
        Assert.Contains(pop.People, p => p.Role == NpcRole.Civilian);
        Assert.Contains(pop.People, p => p.Role == NpcRole.Shopkeeper);
        foreach (var p in pop.People)
        {
            var home = city.Parcels[p.Home];
            if (p.Role == NpcRole.Shopkeeper) Assert.Equal(BuildingType.Shop, city.Parcels[p.Work].Building);
            if (p.Role == NpcRole.Security) Assert.True(CityPopulation.SecurityPosts(city.Parcels[p.Work].Building) > 0);
            if (p.Role == NpcRole.Police) Assert.Equal(BuildingType.Guardhouse, city.Parcels[p.Work].Building);
            Assert.True(CityPopulation.HomeCapacity(home.Building) > 0, $"{p.Name} lives in a {home.Building}");
            if (p.Role == NpcRole.Worker)
                Assert.True(CityPopulation.WorkerPosts(city.Parcels[p.Work].Building) > 0);
            if (p.Role == NpcRole.Civilian) Assert.Equal(-1, p.Work);
        }
        Assert.Equal(pop.People.Count, pop.People.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public void Homes_AreNotOverfilled_AndJobsNotDoubleBooked()
    {
        var city = CityLayout.Generate(5, 5, 4);
        var pop = CityPopulation.Generate(city, 4);
        foreach (var g in pop.People.GroupBy(p => p.Home))
            Assert.InRange(g.Count(), 1, CityPopulation.HomeCapacity(city.Parcels[g.Key].Building));
        foreach (var g in pop.People.Where(p => p.Role == NpcRole.Worker).GroupBy(p => p.Work))
            Assert.InRange(g.Count(), 1, CityPopulation.WorkerPosts(city.Parcels[g.Key].Building));
    }

    [Fact]
    public void Access_IsHomeWorkAndPublicPlaces()
    {
        var city = CityLayout.Generate(5, 5, 5);
        var pop = CityPopulation.Generate(city, 5);
        var worker = pop.People.First(p => p.Role == NpcRole.Worker);
        Assert.True(pop.IsAllowed(worker, city.Parcels[worker.Home]));
        Assert.True(pop.IsAllowed(worker, city.Parcels[worker.Work]));
        Assert.True(pop.IsAllowed(worker, city.Parcels.First(p => p.IsPublic)));
        var stranger = city.Parcels.First(p => !p.IsPublic && p.Id != worker.Home && p.Id != worker.Work);
        Assert.False(pop.IsAllowed(worker, stranger));
        Assert.Contains(worker, pop.PeopleOf(worker.Home));
        Assert.Contains(worker, pop.PeopleOf(worker.Work));
    }

    [Fact]
    public void SameSeed_SamePeople()
    {
        var city = CityLayout.Generate(5, 5, 6);
        Assert.Equal(CityPopulation.Generate(city, 6).People, CityPopulation.Generate(city, 6).People);
    }
}

public class CityNpcTests
{
    private const float SecondsPerHour = 120; // the default: a 48-minute day
    private const float Step = 0.25f;

    private sealed record Sim(CityLayout City, CityPopulation Population, List<CityNpcBrain> Brains, CityClock Clock, CityCrowd Crowd,
        CityDoors Doors);

    private static Sim Start(int size, int seed, float startHour)
    {
        var city = CityLayout.Generate(size, size, seed);
        var pop = CityPopulation.Generate(city, seed);
        var paths = new CityPaths(city);
        var clock = new CityClock(startHour, 1 / SecondsPerHour);
        var brains = pop.People.Select(p => new CityNpcBrain(p, city, paths, seed, clock.Time)).ToList();
        var crowd = new CityCrowd();
        foreach (var b in brains) crowd.Add(b);
        var doors = new CityDoors(city);
        crowd.SetUpDoors(city, doors, clock.Time);
        return new Sim(city, pop, brains, clock, crowd, doors);
    }

    /// <summary>Runs until the clock reaches the hour (game time), calling <paramref name="each"/> after every step.</summary>
    private static void RunUntil(Sim sim, double time, Action? each = null)
    {
        while (sim.Clock.Time < time)
        {
            sim.Clock.Advance(Step);
            sim.Crowd.Step(Step, sim.Clock.Time);
            each?.Invoke();
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void OverADay_NobodyIsEverWhereTheyMayNotBe(int seed)
    {
        var sim = Start(5, seed, 5.5f);
        var problems = new List<string>();
        RunUntil(sim, 5.5 + 24, () =>
        {
            foreach (var b in sim.Brains)
            {
                var parcel = sim.City.ParcelAt(b.X, b.Z);
                if (parcel == null)
                {
                    if (!sim.City.IsStreet(CityLayout.CellAt(b.X, b.Z)) && problems.Count < 10)
                        problems.Add($"{b.Person.Name} off the map at ({b.X:0.0}, {b.Z:0.0})");
                }
                else if (!sim.Population.IsAllowed(b.Person, parcel) && problems.Count < 10)
                    problems.Add($"{b.Person.Name} ({b.Person.Role}) on {parcel.Building} {parcel.Id} at {CityClock.Format(sim.Clock.HourOfDay)}");
            }
        });
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void FollowTheirSchedules(int seed)
    {
        var sim = Start(5, seed, 5.5f);

        // Late morning: workers are at work (tavern staff asleep), day police out on patrol or at the desk, night police at home.
        RunUntil(sim, 11.5);
        bool TavernStaff(CityNpcBrain b) => b.Person.Work >= 0 && sim.City.Parcels[b.Person.Work].Building == BuildingType.Tavern;
        var workers = sim.Brains.Where(b => b.Person.Role == NpcRole.Worker && !TavernStaff(b)).ToList();
        var atWork = workers.Count(b => b.Inside == b.Person.Work);
        Assert.True(atWork >= workers.Count * 0.9, $"{atWork}/{workers.Count} workers at work at 11:30");
        Assert.All(sim.Brains.Where(TavernStaff), b => Assert.Equal(b.Person.Home, b.Inside));
        foreach (var g in sim.Brains.Where(b => b.Person.Role == NpcRole.Police))
        {
            if (g.Person.Shift == 1) Assert.Equal(g.Person.Home, g.Inside);
            else if (g.Person.Desk) Assert.Equal(g.Person.Work, g.Inside);
            else Assert.Equal(-1, g.Inside);
        }
        // Mid-afternoon: civilians are out and about (some in shops, most on the streets).
        RunUntil(sim, 15);
        var civilians = sim.Brains.Where(b => b.Person.Role == NpcRole.Civilian).ToList();
        Assert.True(civilians.Count(b => b.Inside != b.Person.Home) >= civilians.Count * 0.8);

        // Midnight: the taverns are busy, and the station is kept.
        RunUntil(sim, 24);
        var taverns = sim.Brains.Count(b => b.Inside >= 0 && sim.City.Parcels[b.Inside].Building == BuildingType.Tavern);
        Assert.True(taverns >= 5, $"{taverns} people in taverns at midnight");
        Assert.All(sim.Brains.Where(b => b.Person.Role == NpcRole.Police && b.Person.Shift == 1 && b.Person.Desk),
            g => Assert.Equal(g.Person.Work, g.Inside));

        // Small hours: everyone but the night shift is at home (night owls too, by now; tavern staff may still be on the way).
        RunUntil(sim, 24 + 5);
        foreach (var b in sim.Brains.Where(b => !(b.Person.WorksShifts && b.Person.Shift == 1) && !TavernStaff(b)))
            Assert.True(b.Inside == b.Person.Home, $"{b.Person.Name} ({b.Person.Role}) is {b.Activity} at 05:00, inside {b.Inside}");
        Assert.All(sim.Brains.Where(b => b.Person.Role == NpcRole.Police && b.Person.Shift == 1 && !b.Person.Desk), g => Assert.Equal(-1, g.Inside));
        Assert.All(sim.Brains.Where(b => b.Person.Role == NpcRole.Security && b.Person.Shift == 1), g => Assert.Equal(g.Person.Work, g.Inside));
    }

    [Fact]
    public void SomeoneShops_DuringTheDay()
    {
        var sim = Start(5, 2, 8.5f);
        var shopped = new HashSet<int>();
        RunUntil(sim, 17, () =>
        {
            foreach (var b in sim.Brains)
                if (b.Inside >= 0 && sim.City.Parcels[b.Inside].IsPublic && b.Inside != b.Person.Work)
                    shopped.Add(b.Person.Id);
        });
        Assert.True(shopped.Count >= 5, $"{shopped.Count} people visited a shop");
    }

    private static float Dist(float ax, float az, float bx, float bz) =>
        MathF.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Residents_DontStayInsideEachOther(int seed) => DontOverlap(seed);

    private const int MaxOverlaps = 15; // measured: 6-7 with collision, 30-52 without

    private static void DontOverlap(int seed)
    {
        var sim = Start(5, seed, 6.5f); // the morning rush: everyone heads out
        var overlapFor = new Dictionary<(int, int), float>();
        var worst = 0f;
        var worstPair = "";
        var overlaps = 0; // times two people ended up inside each other
        RunUntil(sim, 6.5 + 4, () =>
        {
            var bs = sim.Brains;
            for (var i = 0; i < bs.Count; i++)
                for (var j = i + 1; j < bs.Count; j++)
                {
                    var key = (i, j);
                    if (Dist(bs[i].X, bs[i].Z, bs[j].X, bs[j].Z) < CityCrowd.NpcRadius * 2 - 0.1f
                        && MathF.Abs(bs[i].Y - bs[j].Y) < CityCrowd.SameFloor) // a floor apart isn't in each other's way
                    {
                        if (!overlapFor.ContainsKey(key)) overlaps++;
                        var t = overlapFor[key] = overlapFor.GetValueOrDefault(key) + Step;
                        if (t > worst) (worst, worstPair) = (t, $"{bs[i].Person.Name} ({bs[i].Activity}) / {bs[j].Person.Name} ({bs[j].Activity})");
                    }
                    else overlapFor.Remove(key);
                }
        });
        // Squeezing past someone in a doorway takes a moment; nobody stays inside anyone.
        Assert.True(worst <= CityCrowd.GhostAfterSeconds + 4, $"{worstPair} overlapped for {worst:0.0} s");
        // Only the rare squeeze past someone in a narrow spot; walking through each other would be hundreds.
        Assert.True(overlaps <= MaxOverlaps, $"{overlaps} overlaps in 4 hours");
    }

    [Fact]
    public void Residents_NeverWalkIntoPlayers_EvenInADoorway()
    {
        var sim = Start(5, 3, 6.5f);
        // Players: one standing in the front yard of a home (right in its residents' way), and some on the streets.
        // A worker's home whose whole household is in at 6:30 (nobody on a shift, nobody already gone to work).
        var home = sim.Population.People.First(p => p.Role == NpcRole.Worker
            && sim.Population.PeopleOf(p.Home).All(q => !q.WorksShifts)
            && sim.Brains.Where(b => b.Person.Home == p.Home).All(b => b.Inside == p.Home)).Home;
        var doorway = CityPaths.WayIn(sim.City.Parcels[home], (0, 0))[1];
        var players = new List<(float X, float Z)> { doorway };
        var rng = new Random(5);
        var paths = new CityPaths(sim.City);
        while (players.Count < 8)
        {
            var (x, z) = CityLayout.CellCenter(paths.StreetCells[rng.Next(paths.StreetCells.Count)]);
            (x, z) = (x + (float)rng.NextDouble() * 4 - 2, z + (float)rng.NextDouble() * 4 - 2);
            if (sim.Brains.All(b => Dist(b.X, b.Z, x, z) > 2)) players.Add((x, z));
        }
        sim.Crowd.SetPlayers(players);
        var closest = float.MaxValue;
        RunUntil(sim, 6.5 + 3, () =>
        {
            foreach (var b in sim.Brains)
                foreach (var (px, pz) in players)
                    closest = MathF.Min(closest, Dist(b.X, b.Z, px, pz));
        });
        Assert.True(closest >= CityCrowd.NpcRadius + CityCrowd.PlayerRadius - 0.01f, $"came within {closest:0.00} m of a player");
        // The household behind the blocked gateway is still at home (waiting), not through the player.
        Assert.Contains(sim.Brains, b => b.Person.Home == home && sim.City.ParcelAt(b.X, b.Z)?.Id == home);
    }

    [Fact]
    public void Residents_WalkAroundAPlayerInTheStreet()
    {
        var sim = Start(5, 1, 7.4f);
        // Wait until someone is walking down a street, then stand right in front of them.
        CityNpcBrain? walker = null;
        RunUntil(sim, 7.4 + 0.5, () => walker ??= sim.Brains.FirstOrDefault(b =>
            b.Moving && CityPaths.IsOpenStreet(sim.City, b.X, b.Z, 2) && b.Person.Role != NpcRole.Police));
        Assert.NotNull(walker);
        var ahead = (X: walker!.X - MathF.Sin(walker.Yaw) * 1.2f, Z: walker.Z - MathF.Cos(walker.Yaw) * 1.2f);
        sim.Crowd.SetPlayers([ahead]);
        var (startX, startZ) = (walker.X, walker.Z);
        var closest = float.MaxValue;
        RunUntil(sim, sim.Clock.Time + 20 / SecondsPerHour, // 20 seconds
            () => closest = MathF.Min(closest, Dist(walker.X, walker.Z, ahead.X, ahead.Z)));
        Assert.True(closest >= CityCrowd.NpcRadius + CityCrowd.PlayerRadius - 0.01f, $"came within {closest:0.00} m");
        Assert.True(Dist(walker.X, walker.Z, startX, startZ) > 4, $"{walker.Person.Name} only got {Dist(walker.X, walker.Z, startX, startZ):0.0} m");
        Assert.True(Dist(walker.X, walker.Z, ahead.X, ahead.Z) >= CityCrowd.NpcRadius + CityCrowd.PlayerRadius - 0.01f);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void AtNight_DoorsAreShut_AndHomesLocked(int seed)
    {
        var sim = Start(5, seed, 5.5f);
        var opened = new HashSet<int>();
        sim.Doors.Changed += p => { if (sim.Doors.IsOpen(p)) opened.Add(p); };
        RunUntil(sim, 24 + 5); // the night owls home, the early shift not yet up
        Assert.True(opened.Count > 20, $"only {opened.Count} doors were ever opened");
        for (var p = 0; p < sim.Doors.Count; p++)
        {
            Assert.False(sim.Doors.IsOpen(p), $"{sim.City.Parcels[p].Building} {p} left open at 05:00");
            var sleepers = sim.Brains.Where(b => b.Inside == p && b.Person.Home == p).ToList();
            if (sleepers.Count > 0 && CityPopulation.HomeCapacity(sim.City.Parcels[p].Building) > 0)
                Assert.True(sim.Doors.IsLocked(p), $"{sim.City.Parcels[p].Building} {p} unlocked at night with {sleepers.Count} inside");
        }
    }

    [Fact]
    public void Shops_AreLockedAfterHours()
    {
        var sim = Start(5, 2, 5.5f);
        RunUntil(sim, 21);
        foreach (var shop in sim.City.Parcels.Where(p => p.Building == BuildingType.Shop))
            if (!sim.Brains.Any(b => sim.City.ParcelAt(b.X, b.Z)?.Id == shop.Id))
                Assert.True(sim.Doors.IsLocked(shop.Id), $"shop {shop.Id} is empty but unlocked at 21:00");
    }

    [Fact]
    public void WithoutAKey_ALockedHomeStaysShut()
    {
        var sim = Start(5, 3, 5.5f);
        var home = sim.Population.People.First(p => p.Role == NpcRole.Worker).Home;
        var household = sim.Brains.Where(b => b.Person.Home == home).ToList();
        foreach (var b in household) b.HasKeyTo = _ => false; // they lost their keys
        RunUntil(sim, 13); // everyone's out by now
        Assert.All(household, b => Assert.NotEqual(home, sim.City.ParcelAt(b.X, b.Z)?.Id));
        sim.Doors.Close(home);
        sim.Doors.Lock(home);
        RunUntil(sim, 24 + 2);
        Assert.All(household, b => Assert.NotEqual(home, b.Inside));
        Assert.True(sim.Doors.IsLocked(home));
    }

    [Fact]
    public void TheDead_StayPut_AndDontBlockAnyone()
    {
        var sim = Start(5, 1, 8.5f);
        RunUntil(sim, 9);
        var victim = sim.Brains.First(b => b.Moving);
        victim.Die();
        var (x, z) = (victim.X, victim.Z);
        RunUntil(sim, 11);
        Assert.Equal((x, z), (victim.X, victim.Z));
        Assert.Null(sim.Crowd.Overlapping(sim.Brains.First(b => b != victim), x, z));
    }

    /// <summary>Knocks someone out, drops their body at a spot, wakes them, and records where they walk until back on the street.</summary>
    private static List<(float X, float Z)> WakeAt(Sim sim, CityNpcBrain who, (float X, float Z) spot)
    {
        who.KnockOut();
        Assert.True(who.IsDown);
        who.MoveBody(spot.X, spot.Z, 0);
        RunUntil(sim, sim.Clock.Time + 0.2);
        Assert.Equal(spot, (who.X, who.Z)); // out cold: doesn't move by itself
        who.WakeUp();
        var track = new List<(float X, float Z)>();
        RunUntil(sim, sim.Clock.Time + 0.5, () => track.Add((who.X, who.Z))); // a minute of game time is plenty
        return track;
    }

    [Fact]
    public void WakingUpBehindAHouse_TheyGoRoundIt_AndOutOfTheGate()
    {
        var sim = Start(5, 2, 10);
        var who = sim.Brains.First(b => b.Person.Role == NpcRole.Civilian && !b.Moving);
        // The back of a 2-lot parcel's yard, behind its building.
        var parcel = sim.City.Parcels.First(p => p.Cells.Count == 2);
        var (bx, bz) = parcel.ToWorld(0, -parcel.Variant.Depth - 1.0f);
        Assert.True(parcel.Bounds.Contains(bx, bz) && !parcel.Footprint.Contains(bx, bz));
        var track = WakeAt(sim, who, (bx, bz));
        Assert.DoesNotContain(track, t => parcel.Footprint.Contains(t.X, t.Z));
        Assert.True(sim.City.IsStreet(CityLayout.CellAt(who.X, who.Z)) || who.Inside >= 0, $"{who.Person.Name} ended at ({who.X:0.0}, {who.Z:0.0})");
        // Out through the gate only: never across a fence into another parcel.
        Assert.All(track, t => Assert.True(sim.City.ParcelAt(t.X, t.Z) is not { } q || q == parcel || sim.Population.IsAllowed(who.Person, q)));
    }

    [Fact]
    public void WakingUpInsideSomeonesHouse_TheyLeaveThroughTheDoor()
    {
        var sim = Start(5, 2, 10);
        var who = sim.Brains.First(b => b.Person.Role == NpcRole.Worker);
        var house = sim.City.Parcels.First(p => p.Building == BuildingType.House && p.Id != who.Person.Home);
        sim.Doors.Close(house.Id);
        sim.Doors.Lock(house.Id); // locked in: they can still let themselves out from inside
        var track = WakeAt(sim, who, house.Spots[0]);
        // Inside the building they stay within the walls until the doorway, then cross the yard to the gate.
        var f = house.Footprint;
        foreach (var t in track.Where(t => f.Contains(t.X, t.Z)))
        {
            var (lx, lz) = house.ToLocal(t.X, t.Z);
            var (dx, _) = house.ToLocal(house.Door.X, house.Door.Z);
            Assert.True(lz < -0.3f || MathF.Abs(lx - dx) < 0.8f, $"through the wall at local ({lx:0.0}, {lz:0.0})");
        }
        Assert.Null(sim.City.ParcelAt(track[^1].X, track[^1].Z) is { } end && end == house ? end : null);
        Assert.True(sim.Doors.IsLocked(house.Id), "they should lock the latch behind them");
    }

    [Fact]
    public void AfterWakingUp_TheyCarryOnWithTheirDay()
    {
        var sim = Start(5, 1, 9);
        var who = sim.Brains.First(b => b.Person.Role == NpcRole.Worker && sim.City.Parcels[b.Person.Work].Building != BuildingType.Tavern);
        var (x, z) = CityLayout.CellCenter(new CityPaths(sim.City).StreetCells[^1]);
        WakeAt(sim, who, (x, z));
        RunUntil(sim, 12.5);
        Assert.Equal(who.Person.Work, who.Inside);
    }

    [Fact]
    public void Clock_FormatsAndWraps()
    {
        var clock = new CityClock(23.5f, 1);
        clock.Advance(0.75);
        Assert.Equal("00:15", CityClock.Format(clock.HourOfDay));
        Assert.Equal(24.25, clock.Time, 3);
    }
}

public class CityNightlifeTests
{
    [Theory]
    [InlineData(5, 1)]
    [InlineData(10, 3)]
    public void EveryCity_HasStaffedTaverns_InOneDistrict(int size, int seed)
    {
        var city = CityLayout.Generate(size, size, seed);
        var taverns = city.Parcels.Where(p => p.Building == BuildingType.Tavern).ToList();
        Assert.Equal(Math.Max(1, (int)Math.Round(size * size / 10.0)), taverns.Count);
        Assert.All(taverns, t => Assert.True(t.IsPublic));
        // Close together: every tavern within a few blocks of the first.
        Assert.All(taverns, t => Assert.True(MathF.Abs(t.Bounds.CenterX - taverns[0].Bounds.CenterX) + MathF.Abs(t.Bounds.CenterZ - taverns[0].Bounds.CenterZ) < 150));
        var pop = CityPopulation.Generate(city, seed);
        Assert.All(taverns, t => Assert.Equal(CityPopulation.TavernStaff, pop.PeopleOf(t.Id).Count(p => p.Work == t.Id)));
        // Every station is kept, day and night, by a desk officer who lives elsewhere.
        foreach (var g in city.Parcels.Where(p => p.Building == BuildingType.Guardhouse))
        {
            var officers = pop.People.Where(p => p.Role == NpcRole.Police && p.Work == g.Id).ToList();
            Assert.Contains(officers, o => o.Desk && o.Shift == 0);
            Assert.Contains(officers, o => o.Desk && o.Shift == 1);
            Assert.All(officers, o => Assert.NotEqual(g.Id, o.Home));
        }
    }

    [Fact]
    public void Taverns_AreOpenEveningsAndNights()
    {
        Assert.True(Stealth.IsOpen(BuildingType.Tavern, 23));
        Assert.True(Stealth.IsOpen(BuildingType.Tavern, 1));
        Assert.False(Stealth.IsOpen(BuildingType.Tavern, 10));
        Assert.False(Stealth.IsOpen(BuildingType.Shop, 23));
        Assert.False(Stealth.IsOpen(BuildingType.House, 12));
    }
}
