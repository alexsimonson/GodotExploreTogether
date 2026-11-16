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
        Assert.Equal(city.Parcels.Count(p => p.Building == BuildingType.Guardhouse) * CityPopulation.GuardsPerGuardhouse,
            pop.People.Count(p => p.Role == NpcRole.Guard));
        Assert.Contains(pop.People, p => p.Role == NpcRole.Worker);
        Assert.Contains(pop.People, p => p.Role == NpcRole.Civilian);
        foreach (var p in pop.People)
        {
            var home = city.Parcels[p.Home];
            if (p.Role == NpcRole.Guard)
            {
                Assert.Equal(BuildingType.Guardhouse, home.Building);
                Assert.Equal(p.Home, p.Work);
            }
            else
            {
                Assert.True(CityPopulation.HomeCapacity(home.Building) > 0, $"{p.Name} lives in a {home.Building}");
            }
            if (p.Role == NpcRole.Worker) Assert.True(CityPopulation.Jobs(city.Parcels[p.Work].Building) > 0);
            if (p.Role == NpcRole.Civilian) Assert.Equal(-1, p.Work);
        }
        Assert.Equal(pop.People.Count, pop.People.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public void Homes_AreNotOverfilled_AndJobsNotDoubleBooked()
    {
        var city = CityLayout.Generate(5, 5, 4);
        var pop = CityPopulation.Generate(city, 4);
        foreach (var g in pop.People.Where(p => p.Role != NpcRole.Guard).GroupBy(p => p.Home))
            Assert.InRange(g.Count(), 1, CityPopulation.HomeCapacity(city.Parcels[g.Key].Building));
        foreach (var g in pop.People.Where(p => p.Role == NpcRole.Worker).GroupBy(p => p.Work))
            Assert.InRange(g.Count(), 1, CityPopulation.Jobs(city.Parcels[g.Key].Building));
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

    private sealed record Sim(CityLayout City, CityPopulation Population, List<CityNpcBrain> Brains, CityClock Clock, CityCrowd Crowd);

    private static Sim Start(int size, int seed, float startHour)
    {
        var city = CityLayout.Generate(size, size, seed);
        var pop = CityPopulation.Generate(city, seed);
        var paths = new CityPaths(city);
        var clock = new CityClock(startHour, 1 / SecondsPerHour);
        var brains = pop.People.Select(p => new CityNpcBrain(p, city, paths, seed, clock.Time)).ToList();
        var crowd = new CityCrowd();
        foreach (var b in brains) crowd.Add(b);
        return new Sim(city, pop, brains, clock, crowd);
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

        // Late morning: workers are at work, day guards out on patrol, night guards resting.
        RunUntil(sim, 11.5);
        var workers = sim.Brains.Where(b => b.Person.Role == NpcRole.Worker).ToList();
        var atWork = workers.Count(b => b.Inside == b.Person.Work);
        Assert.True(atWork >= workers.Count * 0.9, $"{atWork}/{workers.Count} workers at work at 11:30");
        foreach (var g in sim.Brains.Where(b => b.Person.Role == NpcRole.Guard))
        {
            if (g.Person.Shift == 0) Assert.Equal(-1, g.Inside);
            else Assert.Equal(g.Person.Home, g.Inside);
        }
        // Mid-afternoon: civilians are out and about (some in shops, most on the streets).
        RunUntil(sim, 15);
        var civilians = sim.Brains.Where(b => b.Person.Role == NpcRole.Civilian).ToList();
        Assert.True(civilians.Count(b => b.Inside != b.Person.Home) >= civilians.Count * 0.8);

        // Night: everyone but the night guards is at home.
        RunUntil(sim, 24 + 3);
        foreach (var b in sim.Brains.Where(b => !(b.Person.Role == NpcRole.Guard && b.Person.Shift == 1)))
            Assert.True(b.Inside == b.Person.Home, $"{b.Person.Name} ({b.Person.Role}) is {b.Activity} at 03:00, inside {b.Inside}");
        Assert.All(sim.Brains.Where(b => b.Person.Role == NpcRole.Guard && b.Person.Shift == 1), g => Assert.Equal(-1, g.Inside));
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
                    if (Dist(bs[i].X, bs[i].Z, bs[j].X, bs[j].Z) < CityCrowd.NpcRadius * 2 - 0.1f)
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
        var home = sim.Population.People.First(p => p.Role == NpcRole.Worker).Home;
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
            b.Moving && CityPaths.IsOpenStreet(sim.City, b.X, b.Z, 2) && b.Person.Role != NpcRole.Guard));
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

    [Fact]
    public void Clock_FormatsAndWraps()
    {
        var clock = new CityClock(23.5f, 1);
        clock.Advance(0.75);
        Assert.Equal("00:15", CityClock.Format(clock.HourOfDay));
        Assert.Equal(24.25, clock.Time, 3);
    }
}
