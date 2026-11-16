namespace ExploreTogether.Core.City;

/// <summary>
/// How lit any point of a city is, from 0 (pitch dark) to 1 (broad daylight). Stealth uses it: residents see much
/// further in the light (see <see cref="Stealth.SightRange"/>). The client draws the same lights.
///
/// - Daylight follows the clock (sunrise about 6:00, sunset about 18:00), down to a faint moonlight at night.
/// - Street lamps stand on about half the street cells, at a sidewalk corner. They come on at dusk and light a
///   circle of <see cref="LampRadius"/> m, brightest under the lamp.
/// - Buildings are lit inside while someone in them is awake at night (and always somewhat by day, through the
///   windows); a lit house also spills a little light into its yard.
/// - Alleys have no lamps and are shaded even by day.
/// </summary>
public sealed class CityLighting
{
    public const float LampRadius = 11f;
    public const float LampBrightness = 0.85f;
    public const float Moonlight = 0.06f;
    /// <summary>Lamp head height (m), for the client.</summary>
    public const float LampHeight = 4.2f;

    private readonly CityLayout _city;
    private readonly Dictionary<Cell, List<(float X, float Z)>> _lampsByCell = new();

    public IReadOnlyList<(float X, float Z)> Lamps { get; }

    public CityLighting(CityLayout city)
    {
        _city = city;
        Lamps = PlaceLamps(city);
        foreach (var lamp in Lamps)
        {
            var c = CityLayout.CellAt(lamp.X, lamp.Z);
            (_lampsByCell.TryGetValue(c, out var list) ? list : _lampsByCell[c] = []).Add(lamp);
        }
    }

    /// <summary>The sun's share of the light, 0 (night) to 1 (day): the same curve the client's sun follows.</summary>
    public static float Daylight(float hour)
    {
        var angle = (hour - 6f) / 12f * MathF.PI;
        return Math.Clamp(MathF.Sin(angle) * 2.5f, 0f, 1f);
    }

    /// <summary>Street lamps burn from dusk to dawn.</summary>
    public static bool LampsOn(float hour) => Daylight(hour) < 0.6f;

    /// <summary>About half the street cells get a lamp, at one sidewalk corner (seeded, so server and client agree).</summary>
    private static List<(float X, float Z)> PlaceLamps(CityLayout city)
    {
        var lamps = new List<(float X, float Z)>();
        for (var x = 0; x < city.CellsX; x++)
            for (var z = 0; z < city.CellsZ; z++)
            {
                var cell = new Cell(x, z);
                if (!city.IsStreet(cell)) continue;
                var h = unchecked((uint)(x * 73856093 ^ z * 19349663 ^ city.Seed * 83492791));
                h ^= h >> 13;
                h *= 0x5bd1e995;
                h ^= h >> 15;
                if (h % 100 >= 55) continue;
                var (cx, cz) = CityLayout.CellCenter(cell);
                var sx = (h & 0x100) != 0 ? 1 : -1;
                var sz = (h & 0x200) != 0 ? 1 : -1;
                lamps.Add((cx + sx * 5.2f, cz + sz * 5.2f));
            }
        return lamps;
    }

    /// <summary>How lit (x, z) is at that hour. <paramref name="parcelLit"/>: is that building lit inside?</summary>
    public float LightAt(float x, float z, float hour, Func<int, bool>? parcelLit = null)
    {
        var day = Daylight(hour);
        var parcel = _city.ParcelAt(x, z);
        var lit = parcel != null && (parcelLit?.Invoke(parcel.Id) ?? false);
        if (parcel != null && parcel.Footprint.Contains(x, z))
            return lit ? 0.75f : 0.03f + 0.5f * day; // indoors: lamps on, or what comes through the windows

        var outdoor = Moonlight + (1 - Moonlight) * day;
        if (_city.AlleyAt(x, z) != null) return outdoor * 0.55f; // shaded, unlit
        var light = outdoor;
        if (LampsOn(hour)) light = MathF.Max(light, LampLightAt(x, z));
        if (lit) light = MathF.Max(light, 0.3f); // light from the windows
        return light;
    }

    /// <summary>The brightest lamp's light at (x, z), when lamps are on.</summary>
    public float LampLightAt(float x, float z)
    {
        var best = 0f;
        var c = CityLayout.CellAt(x, z);
        for (var dx = -1; dx <= 1; dx++)
            for (var dz = -1; dz <= 1; dz++)
            {
                if (!_lampsByCell.TryGetValue(new Cell(c.X + dx, c.Z + dz), out var lamps)) continue;
                foreach (var (lx, lz) in lamps)
                {
                    var d = MathF.Sqrt((lx - x) * (lx - x) + (lz - z) * (lz - z));
                    if (d < LampRadius) best = MathF.Max(best, LampBrightness * (1 - d / LampRadius * 0.85f));
                }
            }
        return best;
    }

    /// <summary>A word for the HUD's light meter.</summary>
    public static string Describe(float light) => light switch
    {
        < 0.15f => "dark",
        < 0.35f => "dim",
        < 0.7f => "lit",
        _ => "bright",
    };
}
