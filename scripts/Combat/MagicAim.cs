using ExploreTogether.Core.Combat;
using Godot;

namespace ExploreTogether.Combat;

/// <summary>
/// Where a magic area lands (port of PlayerGhostAim.DrawGhostAim), shared by the client's ghost preview
/// and the server's cast so they always agree:
/// - aim from the head along the view; the first world surface within 10 m is the spot;
/// - if nothing is hit within 10 m (e.g. looking down a long corridor), it drops to the floor below the
///   10 m point. The original showed nothing in that case, which made the ghost seem to vanish.
/// </summary>
public static class MagicAim
{
    public static Vector3? FindPlacement(PhysicsDirectSpaceState3D space, Vector3 origin, Vector3 direction)
    {
        var end = origin + direction.Normalized() * CombatRules.MagicPlaceRange;
        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(origin, end, collisionMask: 1));
        if (hit.Count > 0) return hit["position"].AsVector3();

        var down = space.IntersectRay(PhysicsRayQueryParameters3D.Create(end, end + Vector3.Down * 20f, collisionMask: 1));
        return down.Count > 0 ? down["position"].AsVector3() : null;
    }

    /// <summary>
    /// The original GhostPortalDefault visual, solid (no transparency) in the given color, which is the
    /// caster's chosen character color: the pentagram model (your Blender-generated pentagram.fbx),
    /// scaled like the prefab (0.25 inside a 3 x 0.1 x 3 parent, so 0.75 across and 0.1 high).
    /// </summary>
    public static Node3D CreatePentagram(Color color)
    {
        var root = new Node3D { Name = "Pentagram" };
        var model = GD.Load<PackedScene>("res://assets/models/pentagram/pentagram.fbx").Instantiate<Node3D>();
        model.Scale = new Vector3(0.75f, 0.1f, 0.75f);
        root.AddChild(model);
        var material = new StandardMaterial3D
        {
            AlbedoColor = new Color(color, 1f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        foreach (var mesh in model.FindChildren("*", nameof(MeshInstance3D), true, false))
            ((MeshInstance3D)mesh).MaterialOverride = material;
        return root;
    }
}
