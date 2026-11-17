using ExploreTogether.Core.City;

namespace ExploreTogether.Core.Tests;

public class FurnitureTests
{
    [Theory]
    [InlineData(5, 1)]
    [InlineData(5, 4)]
    [InlineData(8, 9)]
    public void Buildings_AreFurnished_AndTheSameEveryTime(int size, int seed)
    {
        var city = CityLayout.Generate(size, size, seed);
        var pieces = Furniture.Generate(city);
        Assert.Equal(pieces, Furniture.Generate(city));
        Assert.Equal(Enumerable.Range(0, pieces.Count), pieces.Select(p => p.Id));
        // Most homes get a bed; shops a counter; taverns a bar with stools.
        var homes = city.Parcels.Where(p => CityPopulation.HomeCapacity(p.Building) > 0 && p.Variant.IsGreybox).ToList();
        var withBeds = homes.Count(h => pieces.Any(f => f.Parcel == h.Id && f.Kind == FurnitureKind.Bed));
        Assert.True(withBeds >= homes.Count * 0.8, $"{withBeds}/{homes.Count} homes have a bed");
        Assert.All(city.Parcels.Where(p => p.Building == BuildingType.Tavern && p.Variant.IsGreybox),
            t => Assert.Contains(pieces, f => f.Parcel == t.Id && f.Kind == FurnitureKind.Stool));
        Assert.Contains(pieces, f => f.Kind == FurnitureKind.Chair);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Furniture_KeepsClearOfDoorways_Stairs_AndEachOther(int seed)
    {
        var city = CityLayout.Generate(6, 6, seed);
        var pieces = Furniture.Generate(city);
        foreach (var f in pieces)
        {
            var p = city.Parcels[f.Parcel];
            var interior = p.Interior!;
            Assert.True(interior.Rooms.Any(r => r.Floor == f.Floor && Furniture.Overlaps(r.Rect, f.Rect)), $"{f} is in no room");
            foreach (var part in interior.Partitions.Where(w => w.Floor == f.Floor))
                foreach (var (x, z) in part.Gaps)
                    Assert.True(Furniture.DistanceTo(f.Rect, x, z) >= Furniture.DoorwayClear - 1e-3f, $"{f.Kind} blocks a doorway");
            foreach (var s in interior.Stairs.Where(s => s.FromFloor == f.Floor || s.FromFloor + 1 == f.Floor))
                Assert.False(Furniture.Overlaps(f.Rect, s.Rect), $"{f.Kind} on the stairs");
            if (f.Floor == 0)
                Assert.True(Furniture.DistanceTo(f.Rect, p.Door.X, p.Door.Z) >= Furniture.FrontDoorClear, $"{f.Kind} by the front door");
            foreach (var o in pieces.Where(o => o.Id > f.Id && o.Parcel == f.Parcel && o.Floor == f.Floor))
                Assert.False(Furniture.Overlaps(f.Rect, o.Rect), $"{f.Kind} overlaps {o.Kind}");
        }
    }

    [Fact]
    public void Residents_NeverWalkThroughFurniture()
    {
        var city = CityLayout.Generate(5, 5, 2);
        var pieces = Furniture.Generate(city).Where(f => !f.Pickable).ToList(); // chairs get pushed about; the rest stay put
        var pop = CityPopulation.Generate(city, 2);
        var paths = new CityPaths(city);
        var clock = new CityClock(5.5f, 1 / 120f);
        var brains = pop.People.Select(p => new CityNpcBrain(p, city, paths, 2, clock.Time)).ToList();
        var crowd = new CityCrowd();
        foreach (var b in brains) crowd.Add(b);
        var doors = new CityDoors(city);
        crowd.SetUpDoors(city, doors, clock.Time);
        var hits = new List<string>();
        while (clock.Time < 5.5 + 24)
        {
            clock.Advance(0.25);
            crowd.Step(0.25f, clock.Time);
            foreach (var b in brains)
                foreach (var f in pieces)
                {
                    if (f.Floor != b.Floor || !Shrink(f.Rect).Contains(b.X, b.Z)) continue;
                    if (hits.Count < 10) hits.Add($"{b.Person.Name} ({b.Activity}) inside a {f.Kind} at {CityClock.Format(clock.HourOfDay)}");
                }
        }
        Assert.True(hits.Count == 0, string.Join("\n", hits));

        static GroundRect Shrink(GroundRect r) => new(r.MinX + 0.1f, r.MinZ + 0.1f, r.MaxX - 0.1f, r.MaxZ - 0.1f);
    }
}
