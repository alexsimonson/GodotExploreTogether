using System.Collections.Generic;
using ExploreTogether.Core.City;
using Godot;
using Sides = ExploreTogether.Core.Dungeon.Sides;

namespace ExploreTogether.City;

/// <summary>
/// Dev check (--dev-check-city): raycasts the built city's collision against the layout, per parcel.
/// - Door path: from the street through the fence gate and the doorway, 1 m into the building, nothing is hit.
/// - Fence: beside the gate, a ray from the street hits the fence at the parcel's edge.
/// - Wall: beside the doorway, a ray from the yard hits the building's front wall.
/// </summary>
public static class CityChecks
{
    public static (int Checked, List<string> Failures) Verify(CityLayout city, World3D world)
    {
        var space = world.DirectSpaceState;
        var failures = new List<string>();
        var count = 0;

        Godot.Collections.Dictionary Ray(Vector3 from, Vector3 to) =>
            space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to, collisionMask: 1));

        foreach (var p in city.Parcels)
        {
            var (dx, dz) = Core.Dungeon.MazePieces.Vector(p.Front);
            var outward = new Vector3(dx, 0, dz);
            var along = p.Front is Sides.North or Sides.South ? Vector3.Right : Vector3.Back;
            var door = new Vector3(p.Door.X, 0, p.Door.Z);
            // The street-side edge of the parcel, measured from the door along the outward direction.
            var edgeDist = p.Front switch
            {
                Sides.North => p.Bounds.MaxZ - p.Door.Z,
                Sides.South => p.Door.Z - p.Bounds.MinZ,
                Sides.East => p.Bounds.MaxX - p.Door.X,
                _ => p.Door.X - p.Bounds.MinX,
            };
            var street = door + outward * (edgeDist + 2);
            var label = $"{p.Building} {p.Id}";

            // 1. The way in is open: street -> gate -> doorway -> 1 m inside (below fence height, so the gate is tested).
            count++;
            var hit = Ray(street + Vector3.Up * 0.5f, door - outward * 1.0f + Vector3.Up * 0.5f);
            if (hit.Count > 0)
                failures.Add($"{label}: door path blocked at {(Vector3)hit["position"]}");

            // 2. The fence beside the gate is solid (3.5 m to one side, still in front of the parcel).
            var side = SideOffset(p, 3.5f);
            if (side != 0)
            {
                count++;
                var from = street + along * side + Vector3.Up * 0.5f;
                hit = Ray(from, from - outward * 3f);
                var expected = edgeDist;
                if (hit.Count == 0)
                    failures.Add($"{label}: no fence beside the gate");
                else if (Mathf.Abs(((Vector3)hit["position"] - door).Dot(outward) - expected) > 0.3f)
                    failures.Add($"{label}: fence hit at {(Vector3)hit["position"]}, expected at the parcel edge");
            }

            // 3. The front wall beside the doorway is solid (a ray from 0.8 m in front of it).
            var wallSide = WallOffset(p);
            if (wallSide != 0)
            {
                count++;
                var from = door + outward * 0.8f + along * wallSide + Vector3.Up * 1.5f;
                hit = Ray(from, from - outward * 1.5f);
                if (hit.Count == 0)
                    failures.Add($"{label}: no wall beside the doorway");
            }
        }
        return (count, failures);
    }

    /// <summary>An offset along the street edge from the door that stays on this parcel's front cell, or 0.</summary>
    private static float SideOffset(Parcel p, float distance)
    {
        var cell = CityLayout.CellRect(p.FrontCell);
        var alongX = p.Front is Sides.North or Sides.South;
        var (lo, hi, at) = alongX ? (cell.MinX, cell.MaxX, p.Door.X) : (cell.MinZ, cell.MaxZ, p.Door.Z);
        if (at + distance < hi - 0.3f) return distance;
        if (at - distance > lo + 0.3f) return -distance;
        return 0;
    }

    /// <summary>An offset from the door to a solid part of the front wall (past the doorway), or 0 if there's no room.</summary>
    private static float WallOffset(Parcel p)
    {
        var f = p.Footprint;
        var alongX = p.Front is Sides.North or Sides.South;
        var (lo, hi, at) = alongX ? (f.MinX, f.MaxX, p.Door.X) : (f.MinZ, f.MaxZ, p.Door.Z);
        const float d = 1.3f; // past the 0.8 m half-doorway
        if (at + d < hi - 0.4f) return d;
        if (at - d > lo + 0.4f) return -d;
        return 0;
    }
}
