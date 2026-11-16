namespace ExploreTogether.Core.City;

public enum NpcRole
{
    /// <summary>Lives in a home; spends the day on errands and walks.</summary>
    Civilian,
    /// <summary>Lives in a home; works at a shop, office or warehouse.</summary>
    Worker,
    /// <summary>Posted to a guardhouse (and lives there); patrols nearby streets on shift.</summary>
    Guard,
}

/// <summary>One person in the city. <see cref="Home"/> and <see cref="Work"/> are parcel ids (-1 = none).</summary>
public sealed record Person(int Id, string Name, NpcRole Role, int Home, int Work)
{
    /// <summary>Guards: 0 = day shift (6:00 to 18:00), 1 = night shift.</summary>
    public int Shift => Id % 2;
}

/// <summary>
/// Who lives and works where, generated from a city's buildings (seeded, so server and tests agree).
///
/// Access rule (the basis for trespass): a person may be on their home's parcel, their workplace's parcel,
/// and any public parcel (shops). Guards live at their guardhouse, so that is both.
/// </summary>
public sealed class CityPopulation
{
    public IReadOnlyList<Person> People { get; }
    private readonly Dictionary<int, List<Person>> _byParcel = new();
    private readonly CityLayout _city;

    /// <summary>The default size: about 1.6 people per tile (40 for 5×5).</summary>
    public static int DefaultCount(CityLayout city) => (int)Math.Round(city.Width * city.Height * 1.6);

    public static int HomeCapacity(BuildingType type) => type switch
    {
        BuildingType.Cottage => 2,
        BuildingType.House => 4,
        BuildingType.RowHouse => 3,
        BuildingType.Manor => 4,
        _ => 0,
    };

    public static int Jobs(BuildingType type) => type switch
    {
        BuildingType.Shop => 1,
        BuildingType.Office => 4,
        BuildingType.Warehouse => 3,
        _ => 0,
    };

    public const int GuardsPerGuardhouse = 2;

    private CityPopulation(CityLayout city, List<Person> people)
    {
        _city = city;
        People = people;
        foreach (var p in people)
            foreach (var parcel in new[] { p.Home, p.Work }.Where(x => x >= 0).Distinct())
                (_byParcel.TryGetValue(parcel, out var list) ? list : _byParcel[parcel] = []).Add(p);
    }

    /// <summary>The people who belong on a parcel (residents, workers, posted guards).</summary>
    public IReadOnlyList<Person> PeopleOf(int parcelId) => _byParcel.TryGetValue(parcelId, out var l) ? l : [];

    /// <summary>May this person be on that parcel?</summary>
    public bool IsAllowed(Person person, Parcel parcel) =>
        parcel.Id == person.Home || parcel.Id == person.Work || parcel.IsPublic;

    public static CityPopulation Generate(CityLayout city, int seed, int? count = null)
    {
        var random = new Random(unchecked(seed * 1103515245 + 12345));
        var total = Math.Max(0, count ?? DefaultCount(city));
        var people = new List<Person>();
        var names = new NameMaker(random);

        // Guards first: they're what makes a guardhouse work.
        var guardhouses = city.Parcels.Where(p => p.Building == BuildingType.Guardhouse).ToList();
        foreach (var g in guardhouses)
            for (var i = 0; i < GuardsPerGuardhouse && people.Count < total; i++)
                people.Add(new Person(people.Count, names.Full(), NpcRole.Guard, g.Id, g.Id));

        // Then jobs: about 40% of the rest work, as long as there are jobs.
        var jobs = city.Parcels.SelectMany(p => Enumerable.Repeat(p.Id, Jobs(p.Building)))
            .OrderBy(_ => random.Next()).ToList();
        var remaining = total - people.Count;
        var workers = Math.Min(jobs.Count, (int)Math.Round(remaining * 0.4));
        var homesteaders = new List<(NpcRole Role, int Work)>();
        for (var i = 0; i < workers; i++) homesteaders.Add((NpcRole.Worker, jobs[i]));
        for (var i = workers; i < remaining; i++) homesteaders.Add((NpcRole.Civilian, -1));
        homesteaders = homesteaders.OrderBy(_ => random.Next()).ToList();

        // Households: fill random homes, each with 1..capacity people sharing a family name.
        var homes = city.Parcels.Where(p => HomeCapacity(p.Building) > 0).OrderBy(_ => random.Next()).ToList();
        var next = 0;
        var homeIndex = 0;
        while (next < homesteaders.Count)
        {
            Parcel home;
            int size;
            if (homes.Count == 0)
                break; // a city with no homes (only possible with odd custom catalogs): nobody but guards lives here
            if (homeIndex < homes.Count)
            {
                home = homes[homeIndex++];
                size = random.Next(1, HomeCapacity(home.Building) + 1);
            }
            else
            {
                // More people than room: squeeze the rest into existing homes.
                home = homes[next % homes.Count];
                size = 1;
            }
            var family = names.Family();
            for (var i = 0; i < size && next < homesteaders.Count; i++, next++)
            {
                var (role, work) = homesteaders[next];
                people.Add(new Person(people.Count, $"{names.First()} {family}", role, home.Id, work));
            }
        }
        return new CityPopulation(city, people);
    }

    /// <summary>Simple names from syllables: varied, pronounceable, and not anyone in particular.</summary>
    private sealed class NameMaker(Random random)
    {
        private static readonly string[] Starts = ["Al", "Bra", "Cor", "Da", "El", "Fen", "Gil", "Ha", "Is", "Jo", "Ka", "Lu", "Mar", "Ne", "Os", "Pe", "Ro", "Sa", "Ti", "Vel", "Wy", "Yor", "Ze"];
        private static readonly string[] Ends = ["ric", "na", "den", "la", "mon", "ra", "win", "lo", "dra", "bel", "nor", "sa", "vin", "ta", "ric", "lia", "dus", "mira"];
        private static readonly string[] FamilyStarts = ["Ash", "Brook", "Cobble", "Dun", "East", "Fair", "Green", "Hollow", "Iron", "Kettle", "Long", "Mill", "North", "Oak", "Pebble", "Reed", "Stone", "Thorn", "Wex", "Yarrow"];
        private static readonly string[] FamilyEnds = ["by", "field", "ford", "well", "wood", "worth", "more", "dale", "ridge", "wick", "hurst", "ley"];

        public string First() => Starts[random.Next(Starts.Length)] + Ends[random.Next(Ends.Length)];
        public string Family() => FamilyStarts[random.Next(FamilyStarts.Length)] + FamilyEnds[random.Next(FamilyEnds.Length)];
        public string Full() => $"{First()} {Family()}";
    }
}
