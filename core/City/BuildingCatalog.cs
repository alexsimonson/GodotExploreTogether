namespace ExploreTogether.Core.City;

/// <summary>
/// One building design the city can place: a size, a type (which decides who lives or works there),
/// and how often it's picked. The greybox designs are built in; custom ones come from building scenes
/// (see docs/CITY-BUILDINGS.md).
///
/// Local coordinates (used by <see cref="DoorOffset"/> and <see cref="Spots"/>): the origin is the middle of
/// the building's front edge on the ground; +Z points out of the front (toward the street), +X along the front
/// (to the right when looking at the building from the street, the building's left from inside); the building
/// covers x in [-Width/2, Width/2] and z in [-Depth, 0].
/// </summary>
public sealed record BuildingVariant(
    string Name,
    BuildingType Type,
    float Width,
    float Depth,
    int Floors,
    float DoorOffset = 0,
    int Weight = BuildingCatalog.DefaultWeight)
{
    /// <summary>Spots where people stand inside (local x, z), e.g. by a counter or a bed. Empty: picked automatically.</summary>
    public IReadOnlyList<(float X, float Z)> Spots { get; init; } = [];

    /// <summary>Greybox buildings shrink to fit any lot; custom ones are placed only where they fit as made.</summary>
    public bool IsGreybox => Name.StartsWith(BuildingCatalog.GreyboxPrefix, StringComparison.Ordinal);
}

/// <summary>
/// The building designs a city is made from. Server and client must use the same catalog (they load the same
/// building scenes); <see cref="Checksum"/> travels with the city so a mismatch is caught instead of silently
/// building a different city.
/// </summary>
public sealed class BuildingCatalog
{
    public const string GreyboxPrefix = "greybox:";
    public const int DefaultWeight = 10;

    /// <summary>Sorted by name, so the order (and so the layout) doesn't depend on how they were found.</summary>
    public IReadOnlyList<BuildingVariant> Variants { get; }
    public int Checksum { get; }

    public BuildingCatalog(IEnumerable<BuildingVariant> variants)
    {
        Variants = variants.OrderBy(v => v.Name, StringComparer.Ordinal).ToList();
        if (Variants.Select(v => v.Name).Distinct().Count() != Variants.Count)
            throw new ArgumentException("Building variant names must be unique.");
        // A stable hash (string.GetHashCode is randomized per process, so not that).
        unchecked
        {
            var h = 17;
            foreach (var v in Variants)
            {
                foreach (var c in $"{v.Name}|{v.Type}|{v.Width:0.###}|{v.Depth:0.###}|{v.Floors}|{v.DoorOffset:0.###}|{v.Weight}")
                    h = h * 31 + c;
                foreach (var (x, z) in v.Spots)
                    h = h * 31 + (int)(x * 100) * 7 + (int)(z * 100);
            }
            Checksum = h;
        }
    }

    /// <summary>Only the built-in greybox designs, one per type.</summary>
    public static BuildingCatalog Greybox { get; } = new(GreyboxVariants());

    /// <summary>The greybox designs plus custom ones.</summary>
    public static BuildingCatalog WithCustom(IEnumerable<BuildingVariant> custom) => new(GreyboxVariants().Concat(custom));

    public int IndexOf(string name)
    {
        for (var i = 0; i < Variants.Count; i++)
            if (Variants[i].Name == name) return i;
        return -1;
    }

    private static IEnumerable<BuildingVariant> GreyboxVariants() =>
        Enum.GetValues<BuildingType>().Select(t =>
        {
            var (w, d, floors) = CityLayout.Size(t);
            return new BuildingVariant(GreyboxPrefix + t, t, w, d, floors);
        });
}
