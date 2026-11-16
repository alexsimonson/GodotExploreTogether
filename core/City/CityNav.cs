namespace ExploreTogether.Core.City;

/// <summary>A point on a route; <see cref="Door"/> >= 0 means the leg to it passes through that parcel's door.</summary>
public readonly record struct NavPoint(float X, float Z, int Door = -1, bool Entering = false);

/// <summary>
/// Routes from anywhere to anywhere in a city, for residents off their daily routine (chasing, fleeing, going to
/// look at something). Unlike <see cref="CityPaths"/>, the ends can be anywhere: a yard, inside a building, an
/// alley, the street. A route leaves the start's place the proper way (out of the building through its door, out of
/// the yard through its gate, out of the alley at its mouth), walks the streets, and enters the destination's
/// place the same way.
///
/// With an obstacle test (the server's sight lines through walls), a clear straight line is used directly; residents
/// who can hop a fence (police and the brave, when chasing) treat a low obstacle as clear.
/// </summary>
public static class CityNav
{
    /// <summary>What's between two points: nothing, something low (a fence: can be hopped), or a wall.</summary>
    public enum Obstacle { None, Low, Wall }

    public static List<NavPoint> Route(CityLayout city, CityPaths paths, (float X, float Z) from, (float X, float Z) to,
        (float X, float Z) lane, Func<float, float, float, float, Obstacle>? obstacle = null, bool canHop = false)
    {
        var route = new List<NavPoint>();
        if (obstacle != null)
        {
            var o = obstacle(from.X, from.Z, to.X, to.Z);
            if (o == Obstacle.None || o == Obstacle.Low && canHop && !InsideAny(city, from) && !InsideAny(city, to))
            {
                route.Add(new NavPoint(to.X, to.Z));
                return route;
            }
        }

        var pf = city.ParcelAt(from.X, from.Z);
        var pt = city.ParcelAt(to.X, to.Z);
        bool inF = pf != null && pf.Footprint.Contains(from.X, from.Z), inT = pt != null && pt.Footprint.Contains(to.X, to.Z);

        // Within one parcel: through the door if one end is inside, otherwise straight.
        if (pf != null && pf == pt)
        {
            var way = CityPaths.WayIn(pf, to);
            if (inF && !inT) route.AddRange([new(way[2].X, way[2].Z), new(way[1].X, way[1].Z, pf.Id)]);
            else if (!inF && inT)
            {
                var (pts, _) = CityPaths.WayToStreet(pf, from);
                route.AddRange(pts.Take(pts.Count - 1).Select(p => new NavPoint(p.X, p.Z))); // round the house to the door's front
                route.Add(new NavPoint(way[2].X, way[2].Z, pf.Id, Entering: true));
            }
            route.Add(new NavPoint(to.X, to.Z));
            return route;
        }

        // Out of where they are, to a street cell.
        Cell start;
        if (pf != null)
        {
            var (pts, throughDoor) = CityPaths.WayToStreet(pf, from);
            for (var i = 0; i < pts.Count; i++)
                route.Add(throughDoor && i == 1 ? new NavPoint(pts[i].X, pts[i].Z, pf.Id) : new NavPoint(pts[i].X, pts[i].Z));
            start = CityPaths.StreetOf(pf);
        }
        else if (city.AlleyAt(from.X, from.Z) is { } alleyF)
        {
            var mouth = alleyF.Mouths.MinBy(m => Dist(m, to));
            route.Add(new NavPoint(mouth.X, mouth.Z));
            start = NearestStreet(city, paths, mouth);
        }
        else start = NearestStreet(city, paths, from);

        // Into where they're going, from a street cell.
        Cell end;
        var tail = new List<NavPoint>();
        if (pt != null)
        {
            end = CityPaths.StreetOf(pt);
            var way = CityPaths.WayIn(pt, to);
            if (inT)
                tail.AddRange([new(way[0].X, way[0].Z), new(way[1].X, way[1].Z), new(way[2].X, way[2].Z, pt.Id, Entering: true)]);
            else
            {
                var (pts, _) = CityPaths.WayToStreet(pt, to); // [round the house..., yard, gate] backwards
                for (var i = pts.Count - 1; i >= 0; i--) tail.Add(new NavPoint(pts[i].X, pts[i].Z));
            }
        }
        else if (city.AlleyAt(to.X, to.Z) is { } alleyT)
        {
            var mouth = alleyT.Mouths.MinBy(m => Dist(m, from));
            end = NearestStreet(city, paths, mouth);
            tail.Add(new NavPoint(mouth.X, mouth.Z));
        }
        else end = NearestStreet(city, paths, to);
        tail.Add(new NavPoint(to.X, to.Z));

        foreach (var p in paths.StreetWalk(start, end, lane)) route.Add(new NavPoint(p.X, p.Z));
        route.AddRange(tail);
        return route;
    }

    private static bool InsideAny(CityLayout city, (float X, float Z) p) =>
        city.ParcelAt(p.X, p.Z) is { } parcel && parcel.Footprint.Contains(p.X, p.Z);

    /// <summary>The street cell (x, z) is on, or the nearest one.</summary>
    public static Cell NearestStreet(CityLayout city, CityPaths paths, (float X, float Z) p)
    {
        var c = CityLayout.CellAt(p.X, p.Z);
        if (city.IsStreet(c)) return c;
        return paths.StreetCells.MinBy(s => Dist(CityLayout.CellCenter(s), p));
    }

    private static float Dist((float X, float Z) a, (float X, float Z) b) =>
        MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
}
