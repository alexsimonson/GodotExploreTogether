using ExploreTogether.Core.City;
using ExploreTogether.Core.Dungeon;

namespace ExploreTogether.Core.Tests;

public class CityLayoutTests
{
    public static IEnumerable<object[]> Configs =>
    [
        [5, 5, 0],   // the test city
        [5, 5, 15],
        [10, 10, 15],
        [1, 1, 15],
        [8, 3, 40],
    ];

    private static IEnumerable<CityLayout> Cities(int w, int h, int loops) =>
        Enumerable.Range(0, 20).Select(seed => CityLayout.Generate(w, h, seed, loops));

    [Theory, MemberData(nameof(Configs))]
    public void SameSeed_SameCity(int w, int h, int loops)
    {
        var a = CityMap.Render(CityLayout.Generate(w, h, 99, loops));
        var b = CityMap.Render(CityLayout.Import(CityLayout.Generate(w, h, 99, loops).Export()));
        Assert.Equal(a, b);
    }

    [Fact]
    public void DifferentSeeds_DifferentCities()
    {
        var maps = Enumerable.Range(0, 10).Select(s => CityMap.Render(CityLayout.Generate(5, 5, s)));
        Assert.True(maps.Distinct().Count() > 5);
    }

    [Theory, MemberData(nameof(Configs))]
    public void EveryCell_IsStreetOrInExactlyOneParcel(int w, int h, int loops)
    {
        foreach (var city in Cities(w, h, loops))
        {
            var counts = new Dictionary<Cell, int>();
            foreach (var p in city.Parcels)
                foreach (var c in p.Cells)
                    counts[c] = counts.GetValueOrDefault(c) + 1;
            for (var x = 0; x < city.CellsX; x++)
                for (var z = 0; z < city.CellsZ; z++)
                {
                    var c = new Cell(x, z);
                    Assert.Equal(city.IsStreet(c) ? 0 : 1, counts.GetValueOrDefault(c));
                }
        }
    }

    [Theory, MemberData(nameof(Configs))]
    public void Streets_AreAllConnected(int w, int h, int loops)
    {
        foreach (var city in Cities(w, h, loops))
        {
            var start = new Cell(1, 1);
            var seen = new HashSet<Cell> { start };
            var queue = new Queue<Cell>([start]);
            while (queue.TryDequeue(out var c))
                foreach (var side in new[] { Sides.North, Sides.East, Sides.South, Sides.West })
                {
                    var (dx, dz) = MazePieces.Vector(side);
                    var n = new Cell(c.X + dx, c.Z + dz);
                    if (city.IsStreet(n) && seen.Add(n)) queue.Enqueue(n);
                }
            var all = 0;
            for (var x = 0; x < city.CellsX; x++)
                for (var z = 0; z < city.CellsZ; z++)
                    if (city.IsStreet(new Cell(x, z))) all++;
            Assert.Equal(all, seen.Count);
        }
    }

    [Theory, MemberData(nameof(Configs))]
    public void Parcels_AreLinesOfOneToThreeCells(int w, int h, int loops)
    {
        foreach (var city in Cities(w, h, loops))
            foreach (var p in city.Parcels)
            {
                Assert.InRange(p.Cells.Count, 1, 3);
                var b = p.Bounds;
                Assert.Equal(p.Cells.Count * CityLayout.CellSize * CityLayout.CellSize, b.Width * b.Depth, 3);
            }
    }

    [Theory, MemberData(nameof(Configs))]
    public void EveryParcel_FrontsAStreet_WithItsDoor(int w, int h, int loops)
    {
        foreach (var city in Cities(w, h, loops))
            foreach (var p in city.Parcels)
            {
                Assert.Contains(p.FrontCell, p.Cells);
                var (dx, dz) = MazePieces.Vector(p.Front);
                Assert.True(city.IsStreet(new Cell(p.FrontCell.X + dx, p.FrontCell.Z + dz)));
                // The door is on the building's street face, in front of the front cell, with room for the full gate gap.
                var cell = CityLayout.CellRect(p.FrontCell);
                Assert.True(p.Front is Sides.North or Sides.South
                    ? p.Door.X >= cell.MinX + 1.2f && p.Door.X <= cell.MaxX - 1.2f
                    : p.Door.Z >= cell.MinZ + 1.2f && p.Door.Z <= cell.MaxZ - 1.2f, $"{p.Building} door {p.Door} vs {cell}");
            }
    }

    [Theory, MemberData(nameof(Configs))]
    public void Buildings_StayInsideTheirFences(int w, int h, int loops)
    {
        foreach (var city in Cities(w, h, loops))
            foreach (var p in city.Parcels)
            {
                var (f, b) = (p.Footprint, p.Bounds);
                Assert.True(f.Width >= 5 && f.Depth >= 5, $"tiny building {f}");
                Assert.True(f.MinX >= b.MinX + 0.99f && f.MaxX <= b.MaxX - 0.99f && f.MinZ >= b.MinZ + 0.99f && f.MaxZ <= b.MaxZ - 0.99f,
                    $"{p.Building} {f} sticks out of {b}");
            }
    }

    [Theory, MemberData(nameof(Configs))]
    public void ParcelAt_FindsTheParcelOverItsWholeArea(int w, int h, int loops)
    {
        foreach (var city in Cities(w, h, loops).Take(3))
            foreach (var p in city.Parcels)
            {
                var b = p.Bounds;
                Assert.Same(p, city.ParcelAt(b.CenterX, b.CenterZ));
                Assert.Same(p, city.ParcelAt(b.MinX + 0.01f, b.MinZ + 0.01f));
                Assert.Same(p, city.ParcelAt(b.MaxX - 0.01f, b.MaxZ - 0.01f));
                Assert.Same(p, city.ParcelAt(p.Footprint.CenterX, p.Footprint.CenterZ));
            }
    }

    [Fact]
    public void TileCenters_AreStreets_AndTheCityStartsAtTheOrigin()
    {
        var city = CityLayout.Generate(5, 5, 7);
        Assert.Null(city.ParcelAt(0, 0));
        Assert.True(city.IsStreet(CityLayout.CellAt(0, 0)));
        for (var tx = 0; tx < 5; tx++)
            for (var tz = 0; tz < 5; tz++)
                Assert.Null(city.ParcelAt(tx * CityLayout.TileSize, tz * CityLayout.TileSize));
    }

    [Fact]
    public void LoopPercent_AddsStreets_ZeroKeepsThePureMaze()
    {
        static int Links(CityLayout c)
        {
            var n = 0;
            for (var x = 0; x < c.Width; x++)
                for (var z = 0; z < c.Height; z++)
                    n += (c.TilesLinkedEast(x, z) ? 1 : 0) + (c.TilesLinkedNorth(x, z) ? 1 : 0);
            return n;
        }
        Assert.Equal(24, Links(CityLayout.Generate(5, 5, 3, loopPercent: 0))); // a spanning tree of 25 tiles
        Assert.True(Links(CityLayout.Generate(5, 5, 3, loopPercent: 30)) > 24);
    }

    [Fact]
    public void HasGuardhouses_Spread_AndAMixOfBuildings()
    {
        foreach (var city in Cities(10, 10, 15))
        {
            var types = city.Parcels.GroupBy(p => p.Building).ToDictionary(g => g.Key, g => g.Count());
            Assert.InRange(types.GetValueOrDefault(BuildingType.Guardhouse), 10, 12);
            Assert.True(types.Count >= 6, string.Join(", ", types));
        }
        foreach (var city in Cities(5, 5, 15))
            Assert.Equal(3, city.Parcels.Count(p => p.Building == BuildingType.Guardhouse));
    }
}
