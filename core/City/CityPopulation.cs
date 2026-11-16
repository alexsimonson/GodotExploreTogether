namespace ExploreTogether.Core.City;

/// <summary>
/// What a resident does in the city, which decides their day and how they react to trouble (see
/// <see cref="Stealth"/>). Values are replicated as ints: never reorder.
/// </summary>
public enum NpcRole
{
    /// <summary>Lives in a home; spends the day on errands and walks. Minds their own home.</summary>
    Civilian,
    /// <summary>Lives in a home; works at an office or warehouse. Minds their home and workplace.</summary>
    Worker,
    /// <summary>Posted to a police station (a guardhouse, where they live); patrols nearby streets on shift.
    /// Authority everywhere: investigates, arrests, and uses force on those who resist.</summary>
    Police,
    /// <summary>Lives in a home; keeps a shop (opens it in the morning, locks it at night) and defends it.</summary>
    Shopkeeper,
    /// <summary>Private guard posted at a manor, office or warehouse on shift. Authority over that property only.</summary>
    Security,
}

/// <summary>Personality: how a resident's reactions differ from others in the same role.</summary>
[Flags]
public enum NpcTraits
{
    None = 0,
    /// <summary>Confronts, grabs, fights back; slow to flee.</summary>
    Brave = 1,
    /// <summary>Flees at the first sign of trouble; never fights.</summary>
    Timid = 2,
    /// <summary>Notices more, minds other people's business (watches, reports trespass on neighbors' property).</summary>
    Nosy = 4,
    /// <summary>Carries a weapon (civilians, workers, shopkeepers) and may use it.</summary>
    Armed = 8,
    /// <summary>Sleeps through noise that would wake others.</summary>
    HeavySleeper = 16,
}

/// <summary>One person in the city. <see cref="Home"/> and <see cref="Work"/> are parcel ids (-1 = none).</summary>
public sealed record Person(int Id, string Name, NpcRole Role, int Home, int Work)
{
    /// <summary>Police and security: 0 = day shift (6:00 to 18:00), 1 = night shift.</summary>
    public int Shift => Id % 2;

    public NpcTraits Traits { get; init; }

    /// <summary>Strength in a struggle, 0..1: how hard they are to hold, and how hard they hold on.</summary>
    public float Grit { get; init; } = 0.5f;

    public bool Has(NpcTraits trait) => (Traits & trait) != 0;

    /// <summary>Police anywhere; everyone else on their own home or workplace (security: their post).</summary>
    public bool HasAuthorityOver(int parcel) =>
        Role == NpcRole.Police || parcel >= 0 && (parcel == Home || parcel == Work);

    /// <summary>Works shifts (police and security), so is sometimes on duty at night.</summary>
    public bool WorksShifts => Role is NpcRole.Police or NpcRole.Security;
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

    /// <summary>Ordinary jobs (workers) a building offers. A shop's one job is its shopkeeper.</summary>
    public static int Jobs(BuildingType type) => type switch
    {
        BuildingType.Office => 4,
        BuildingType.Warehouse => 3,
        _ => 0,
    };

    /// <summary>Security guards a property may hire (each post is filled about 60% of the time).</summary>
    public static int SecurityPosts(BuildingType type) => type switch
    {
        BuildingType.Manor => 1,
        BuildingType.Office => 1,
        BuildingType.Warehouse => 1,
        _ => 0,
    };

    public const int PolicePerStation = 2;

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

        // Police first: they're what makes a station work.
        var stations = city.Parcels.Where(p => p.Building == BuildingType.Guardhouse).ToList();
        foreach (var g in stations)
            for (var i = 0; i < PolicePerStation && people.Count < total; i++)
                people.Add(new Person(people.Count, names.Full(), NpcRole.Police, g.Id, g.Id));

        // Then jobs, about half of the rest: every shop gets its keeper first, then security posts, then
        // ordinary jobs, as long as there are people.
        var keepers = city.Parcels.Where(p => p.Building == BuildingType.Shop).Select(p => (NpcRole.Shopkeeper, p.Id))
            .OrderBy(_ => random.Next()).ToList();
        var posts = city.Parcels.SelectMany(p => Enumerable.Repeat(p.Id, SecurityPosts(p.Building)))
            .Where(_ => random.Next(100) < 60).Select(id => (NpcRole.Security, id)).OrderBy(_ => random.Next()).ToList();
        var ordinary = city.Parcels.SelectMany(p => Enumerable.Repeat(p.Id, Jobs(p.Building)))
            .Select(id => (NpcRole.Worker, id)).OrderBy(_ => random.Next()).ToList();
        // Nearly half the jobs go to ordinary workers (offices and warehouses need staff too), then shops, then posts.
        var remaining = total - people.Count;
        var employed = Math.Min(keepers.Count + posts.Count + ordinary.Count, (int)Math.Round(remaining * 0.5));
        var staff = Math.Min(ordinary.Count, (int)Math.Round(employed * 0.45));
        var jobs = ordinary.Take(staff).Concat(keepers).Concat(posts).Concat(ordinary.Skip(staff)).Take(employed).ToList();
        var homesteaders = new List<(NpcRole Role, int Work)>();
        for (var i = 0; i < employed; i++) homesteaders.Add(jobs[i]);
        for (var i = employed; i < remaining; i++) homesteaders.Add((NpcRole.Civilian, -1));
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
        return new CityPopulation(city, people.Select(p => WithTraits(p, seed)).ToList());
    }

    /// <summary>
    /// Personality, from its own seeded random (so adding traits never changes who lives where). Odds by role:
    /// police and security are mostly brave and never timid; shopkeepers often keep a weapon; civilians vary most.
    /// </summary>
    public static Person WithTraits(Person p, int seed)
    {
        var r = new Random(unchecked(seed * 486187739 + p.Id * 16777619 + 11));
        int Roll() => r.Next(100);
        var (brave, timid, nosy, armed) = p.Role switch
        {
            NpcRole.Police => (70, 0, 50, 100),
            NpcRole.Security => (55, 0, 40, 100),
            NpcRole.Shopkeeper => (35, 20, 30, 40),
            NpcRole.Worker => (20, 30, 15, 12),
            _ => (15, 35, 30, 8),
        };
        var traits = NpcTraits.None;
        if (Roll() < brave) traits |= NpcTraits.Brave;
        else if (Roll() < timid) traits |= NpcTraits.Timid;
        if (Roll() < nosy) traits |= NpcTraits.Nosy;
        if (Roll() < armed) traits |= NpcTraits.Armed;
        if (Roll() < 15) traits |= NpcTraits.HeavySleeper;
        var grit = 0.25f + (float)r.NextDouble() * 0.5f
                   + (p.Role is NpcRole.Police or NpcRole.Security ? 0.2f : 0)
                   + ((traits & NpcTraits.Brave) != 0 ? 0.1f : 0) - ((traits & NpcTraits.Timid) != 0 ? 0.1f : 0);
        return p with { Traits = traits, Grit = Math.Clamp(grit, 0.1f, 1f) };
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
