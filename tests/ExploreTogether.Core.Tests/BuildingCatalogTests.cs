using ExploreTogether.Core.City;

namespace ExploreTogether.Core.Tests;

public class BuildingCatalogTests
{
    private static readonly BuildingVariant Porch =
        new("ExampleCottage", BuildingType.Cottage, 8, 7, 1, DoorOffset: -1.5f, Weight: 30)
        {
            Spots = [(2.5f, -5.5f), (-2.5f, -2f)],
        };

    private static readonly BuildingVariant TooBig = new("Castle", BuildingType.House, 40, 40, 4, Weight: 1000);

    private static readonly BuildingCatalog Custom = BuildingCatalog.WithCustom([Porch, TooBig]);

    private static IEnumerable<CityLayout> Cities(BuildingCatalog catalog, string? favor = null) =>
        Enumerable.Range(0, 15).Select(seed => CityLayout.Generate(6, 6, seed, 15, catalog, favor));

    [Fact]
    public void CustomDesigns_ArePlaced_AtTheirOwnSize()
    {
        var placed = Cities(Custom).SelectMany(c => c.Parcels).Where(p => p.Variant == Porch).ToList();
        Assert.NotEmpty(placed);
        foreach (var p in placed)
        {
            Assert.Equal(BuildingType.Cottage, p.Building);
            var alongX = p.Front is Dungeon.Sides.North or Dungeon.Sides.South;
            Assert.Equal(8, alongX ? p.Footprint.Width : p.Footprint.Depth, 3);
            Assert.Equal(7, alongX ? p.Footprint.Depth : p.Footprint.Width, 3);
        }
    }

    [Fact]
    public void DesignsThatDontFit_AreNeverPlaced()
    {
        Assert.DoesNotContain(Cities(Custom).SelectMany(c => c.Parcels), p => p.Variant == TooBig);
    }

    [Fact]
    public void CustomDoor_IsAtItsOffset_AndInFrontOfTheGate()
    {
        foreach (var p in Cities(Custom, favor: "ExampleCottage").SelectMany(c => c.Parcels).Where(p => p.Variant == Porch))
        {
            var (x, z) = p.ToWorld(Porch.DoorOffset, 0);
            Assert.Equal(p.Door.X, x, 3);
            Assert.Equal(p.Door.Z, z, 3);
            var cell = CityLayout.CellRect(p.FrontCell);
            Assert.True(p.Front is Dungeon.Sides.North or Dungeon.Sides.South
                ? p.Door.X >= cell.MinX + 1.2f && p.Door.X <= cell.MaxX - 1.2f
                : p.Door.Z >= cell.MinZ + 1.2f && p.Door.Z <= cell.MaxZ - 1.2f);
        }
    }

    [Fact]
    public void Spots_AreTheDesignsOwn_AndInsideTheBuilding()
    {
        foreach (var city in Cities(Custom))
            foreach (var p in city.Parcels)
            {
                Assert.NotEmpty(p.Spots);
                if (p.Variant == Porch) Assert.Equal(2, p.Spots.Count);
                foreach (var (x, z) in p.Spots)
                    Assert.True(p.Footprint.Contains(x, z), $"{p.Variant.Name} spot ({x}, {z}) outside {p.Footprint}");
            }
    }

    [Fact]
    public void Favor_PlacesTheDesignWhereverItFits()
    {
        var normal = Cities(Custom).Sum(c => c.Parcels.Count(p => p.Variant == Porch));
        var favored = Cities(Custom, favor: "ExampleCottage").Sum(c => c.Parcels.Count(p => p.Variant == Porch));
        Assert.True(favored > normal * 2, $"{favored} vs {normal}");
        Assert.Throws<ArgumentException>(() => CityLayout.Generate(5, 5, 1, 15, Custom, "NoSuchBuilding"));
    }

    [Fact]
    public void Import_NeedsTheSameCatalog()
    {
        var city = CityLayout.Generate(5, 5, 4, 15, Custom, "ExampleCottage");
        var again = CityLayout.Import(city.Export(), Custom);
        Assert.Equal(CityMap.Render(city), CityMap.Render(again));
        Assert.Equal(city.FavoredVariant, again.FavoredVariant);
        Assert.Throws<ArgumentException>(() => CityLayout.Import(city.Export(), BuildingCatalog.Greybox));
    }

    [Fact]
    public void Checksum_IgnoresOrder_ButNotChanges()
    {
        var a = BuildingCatalog.WithCustom([Porch, TooBig]);
        var b = BuildingCatalog.WithCustom([TooBig, Porch]);
        var c = BuildingCatalog.WithCustom([Porch with { Width = 8.5f }, TooBig]);
        Assert.Equal(a.Checksum, b.Checksum);
        Assert.NotEqual(a.Checksum, c.Checksum);
        Assert.Throws<ArgumentException>(() => BuildingCatalog.WithCustom([Porch, Porch]));
    }

    [Fact]
    public void LotSizesFor_MatchesWhereTypesAreActuallyBuilt()
    {
        var seen = new Dictionary<BuildingType, HashSet<int>>();
        foreach (var city in Enumerable.Range(0, 30).Select(s => CityLayout.Generate(8, 8, s)))
            foreach (var p in city.Parcels)
                (seen.TryGetValue(p.Building, out var set) ? set : seen[p.Building] = []).Add(p.Cells.Count);
        foreach (var (type, sizes) in seen)
            Assert.True(sizes.SetEquals(CityLayout.LotSizesFor(type)),
                $"{type}: built on {string.Join(",", sizes.Order())}, listed {string.Join(",", CityLayout.LotSizesFor(type))}");
    }

    [Fact]
    public void FitsOnLots_ReportsWhereADesignFits()
    {
        Assert.Contains(1, CityLayout.FitsOnLots(Porch));
        Assert.Empty(CityLayout.FitsOnLots(TooBig));
        var wide = new BuildingVariant("Hall", BuildingType.Office, 30, 9, 2);
        Assert.Equal([3], CityLayout.FitsOnLots(wide));
    }

    [Fact]
    public void GreyboxCatalog_HasOneDesignPerType()
    {
        Assert.Equal(Enum.GetValues<BuildingType>().Length, BuildingCatalog.Greybox.Variants.Count);
        Assert.All(BuildingCatalog.Greybox.Variants, v => Assert.True(v.IsGreybox));
    }
}
