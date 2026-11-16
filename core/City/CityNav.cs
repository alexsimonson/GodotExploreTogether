namespace ExploreTogether.Core.City;

/// <summary>
/// A point on a route; <see cref="Door"/> >= 0 means the leg to it passes through that parcel's door.
/// <see cref="Y"/> is the floor's height there (a leg between two heights is a flight of stairs).
/// </summary>
public readonly record struct NavPoint(float X, float Z, int Door = -1, bool Entering = false, float Y = 0)
{
    public static implicit operator NavPoint(Place p) => new(p.X, p.Z, Y: p.Y);
}

/// <summary>
/// Routes from anywhere to anywhere in a city, for residents off their daily routine (chasing, fleeing, going to
/// look at something). Unlike <see cref="CityPaths"/>, the ends can be anywhere: a yard, a room on any floor, an
/// alley, the street. A route leaves the start's place the proper way (through the rooms and down the stairs to the
/// front door, out of the yard through its gate, out of the alley at its mouth), walks the streets, and enters the
/// destination's place the same way.
///
/// With an obstacle test (the server's sight lines through walls, at a height), a clear straight line on one floor is
/// used directly; residents who can hop a fence (when chasing or fleeing) treat a low obstacle as clear.
/// </summary>
public static class CityNav
{
    /// <summary>What's between two points: nothing, something low (a fence: can be hopped), or a wall.</summary>
    public enum Obstacle { None, Low, Wall }

    /// <summary>Tests the straight line (ax, az) to (bx, bz) at floor height y.</summary>
    public delegate Obstacle ObstacleTest(float ax, float az, float bx, float bz, float y);

    public static List<NavPoint> Route(CityLayout city, CityPaths paths, (float X, float Z) from, (float X, float Z) to,
        (float X, float Z) lane, ObstacleTest? obstacle = null, bool canHop = false) =>
        Route(city, paths, new Place(from.X, from.Z), new Place(to.X, to.Z), lane, obstacle, canHop);

    public static List<NavPoint> Route(CityLayout city, CityPaths paths, Place origin, Place dest,
        (float X, float Z) lane, ObstacleTest? obstacle = null, bool canHop = false)
    {
        var route = new List<NavPoint>();
        if (obstacle != null && origin.Floor == dest.Floor)
        {
            var o = obstacle(origin.X, origin.Z, dest.X, dest.Z, origin.Y);
            if (o == Obstacle.None || o == Obstacle.Low && canHop && !InsideAny(city, origin) && !InsideAny(city, dest))
            {
                route.Add(dest);
                return route;
            }
        }

        var pf = city.ParcelAt(origin.X, origin.Z);
        var pt = city.ParcelAt(dest.X, dest.Z);
        bool inF = pf != null && pf.Footprint.Contains(origin.X, origin.Z), inT = pt != null && pt.Footprint.Contains(dest.X, dest.Z);
        if (!inF) origin = origin with { Floor = 0 };
        if (!inT) dest = dest with { Floor = 0 };

        // Within one parcel: through the rooms, and the front door if one end is outside.
        if (pf != null && pf == pt)
        {
            var way = CityPaths.WayIn(pf, (dest.X, dest.Z));
            if (inF && inT)
            {
                route.AddRange(pf.RouteInside(origin, dest).Select(p => (NavPoint)p));
                return route;
            }
            if (inF)
            {
                route.AddRange(pf.RouteInside(origin, pf.JustInside).Select(p => (NavPoint)p));
                route.Add(new NavPoint(way[1].X, way[1].Z, pf.Id));
            }
            else
            {
                var (pts, _) = CityPaths.WayToStreet(pf, (origin.X, origin.Z));
                route.AddRange(pts.Take(pts.Count - 1).Select(p => new NavPoint(p.X, p.Z))); // round the house dest the door's front
                route.Add(new NavPoint(way[2].X, way[2].Z, pf.Id, Entering: true));
                route.AddRange(pf.RouteInside(pf.JustInside, dest).Select(p => (NavPoint)p));
                return route;
            }
            route.Add(dest);
            return route;
        }

        // Out of where they are, dest a street cell.
        Cell start;
        if (pf != null)
        {
            var (pts, throughDoor) = CityPaths.WayToStreet(pf, (origin.X, origin.Z));
            if (throughDoor)
            {
                route.AddRange(pf.RouteInside(origin, pf.JustInside).Select(p => (NavPoint)p));
                route.Add(new NavPoint(pts[1].X, pts[1].Z, pf.Id));
                route.Add(new NavPoint(pts[2].X, pts[2].Z));
            }
            else route.AddRange(pts.Select(p => new NavPoint(p.X, p.Z)));
            start = CityPaths.StreetOf(pf);
        }
        else if (city.AlleyAt(origin.X, origin.Z) is { } alleyF)
        {
            var mouth = alleyF.Mouths.MinBy(m => Dist(m, (dest.X, dest.Z)));
            route.Add(new NavPoint(mouth.X, mouth.Z));
            start = NearestStreet(city, paths, mouth);
        }
        else start = NearestStreet(city, paths, (origin.X, origin.Z));

        // Into where they're going, origin a street cell.
        Cell end;
        var tail = new List<NavPoint>();
        if (pt != null)
        {
            end = CityPaths.StreetOf(pt);
            var way = CityPaths.WayIn(pt, (dest.X, dest.Z));
            if (inT)
            {
                tail.AddRange([new(way[0].X, way[0].Z), new(way[1].X, way[1].Z), new(way[2].X, way[2].Z, pt.Id, Entering: true)]);
                tail.AddRange(pt.RouteInside(pt.JustInside, dest).Select(p => (NavPoint)p));
            }
            else
            {
                var (pts, _) = CityPaths.WayToStreet(pt, (dest.X, dest.Z)); // [round the house..., yard, gate] backwards
                for (var i = pts.Count - 1; i >= 0; i--) tail.Add(new NavPoint(pts[i].X, pts[i].Z));
                tail.Add(dest);
            }
        }
        else if (city.AlleyAt(dest.X, dest.Z) is { } alleyT)
        {
            var mouth = alleyT.Mouths.MinBy(m => Dist(m, (origin.X, origin.Z)));
            end = NearestStreet(city, paths, mouth);
            tail.Add(new NavPoint(mouth.X, mouth.Z));
            tail.Add(dest);
        }
        else
        {
            end = NearestStreet(city, paths, (dest.X, dest.Z));
            tail.Add(dest);
        }

        foreach (var p in paths.StreetWalk(start, end, lane)) route.Add(new NavPoint(p.X, p.Z));
        route.AddRange(tail);
        return route;
    }

    private static bool InsideAny(CityLayout city, Place p) =>
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
