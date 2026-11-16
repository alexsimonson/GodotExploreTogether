using System.Collections.Generic;
using ExploreTogether.Core.Dungeon;
using Godot;

namespace ExploreTogether.Dungeon;

/// <summary>
/// Dev check (--dev-check-walls): verifies the built dungeon geometry matches the maze layout.
/// From each cell's center, casts a ray toward each side at chest height: a side with a
/// neighbor must be open, and a side without one must hit a wall within the cell.
/// This checks piece selection, rotation and placement in the engine itself.
/// </summary>
public static class DungeonChecks
{
    public static (int Checked, List<string> Failures) VerifyWalls(MazeLayout layout, World3D world)
    {
        var space = world.DirectSpaceState;
        var failures = new List<string>();
        var count = 0;
        const float cell = MazeLayout.CellSize;

        foreach (var node in layout.Nodes)
        {
            var center = new Vector3(node.mazePosition.X * cell, 1.5f, node.mazePosition.Z * cell);
            var neighbors = MazePieces.NeighborSides(node);
            foreach (var side in new[] { Sides.North, Sides.East, Sides.South, Sides.West })
            {
                var (vx, vz) = MazePieces.Vector(side);
                // Reach just past this cell's edge (0.2 m), but not into the next cell's far wall.
                var to = center + new Vector3(vx, 0, vz) * (cell / 2 + 0.2f);
                var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(center, to, collisionMask: 1)); // world only
                var open = (neighbors & side) != 0;
                count++;
                if (open == (hit.Count > 0))
                    failures.Add($"node {node.index} at ({node.mazePosition.X},{node.mazePosition.Z}) {side}: " +
                                 $"expected {(open ? "open" : "wall")}, got {(hit.Count > 0 ? "wall" : "open")}");
            }
        }
        return (count, failures);
    }
}
