using ExploreTogether.Core.City;
using ExploreTogether.Dungeon;
using Godot;

namespace ExploreTogether.City;

/// <summary>
/// A building's front door: a panel on a hinge in the doorway, swinging one way only (into the building).
/// Built with the city on server and client; its state comes from the server's <see cref="CityDoors"/>.
///
/// The node sits at the doorway's middle on the building's front face, turned like the building (+Z toward the
/// street). The hinge is on the left side as seen from the street; opening turns the panel 90 degrees inward.
/// Collision follows the panel, so a closed door blocks players (and bullets), an open one stands against the wall.
///
/// Interaction: E opens or closes it (if it isn't locked). L locks or unlocks it: with a key from outside, or
/// freely from inside.
/// </summary>
public partial class CityDoor : Interactable
{
    public const string Prefix = "door:";
    public const float Width = 1.6f, Height = 2.4f;
    private const float Thickness = 0.06f, Inset = 0.15f, OpenDegrees = 90f, SwingSeconds = 0.5f;

    public int ParcelId { get; private set; }
    public bool IsOpen { get; private set; }
    public bool IsLocked { get; private set; }

    private Node3D _hinge = null!;
    private CollisionShape3D _shape = null!; // a direct child of the body (shapes must be), moved along with the hinge
    private float _angle; // current swing, degrees (0 = closed)

    private static StandardMaterial3D? _wood, _metal;

    public static string IdFor(int parcel) => $"{Prefix}{parcel}";

    public static bool TryParse(string interactionId, out int parcel)
    {
        parcel = -1;
        return interactionId.StartsWith(Prefix) && int.TryParse(interactionId[Prefix.Length..], out parcel);
    }

    public static CityDoor Create(Parcel p)
    {
        var door = new CityDoor
        {
            Name = $"Door{p.Id}",
            ParcelId = p.Id,
            InteractionId = IdFor(p.Id),
            Position = new Vector3(p.Door.X, 0, p.Door.Z),
            Rotation = new Vector3(0, p.Yaw, 0),
        };
        _wood ??= new StandardMaterial3D { AlbedoColor = new Color(0.42f, 0.27f, 0.15f), Roughness = 0.8f };
        _metal ??= new StandardMaterial3D { AlbedoColor = new Color(0.75f, 0.65f, 0.35f), Metallic = 0.8f, Roughness = 0.35f };

        door._hinge = new Node3D { Name = "Hinge", Position = new Vector3(-Width / 2, 0, -Inset) };
        door.AddChild(door._hinge);
        var size = new Vector3(Width - 0.04f, Height - 0.04f, Thickness);
        var center = new Vector3(Width / 2, Height / 2, 0);
        door._hinge.AddChild(new MeshInstance3D { Name = "Panel", Mesh = new BoxMesh { Size = size, Material = _wood }, Position = center });
        door._shape = new CollisionShape3D { Name = "Shape", Shape = new BoxShape3D { Size = size } };
        door.AddChild(door._shape);
        foreach (var side in new[] { 1f, -1f })
            door._hinge.AddChild(new MeshInstance3D
            {
                Name = side > 0 ? "KnobOut" : "KnobIn",
                Mesh = new SphereMesh { Radius = 0.05f, Height = 0.1f, Material = _metal },
                Position = new Vector3(Width - 0.2f, 1.0f, side * 0.06f),
            });
        door.SetAngle(0);
        door.UpdatePrompt();
        return door;
    }

    /// <summary>Applies the server's state. Clients swing the panel over half a second; the server snaps it.</summary>
    public void SetState(bool open, bool locked, bool animate)
    {
        (IsOpen, IsLocked) = (open, locked);
        if (!animate) SetAngle(open ? OpenDegrees : 0);
        UpdatePrompt();
    }

    public override void _Process(double delta)
    {
        var target = IsOpen ? OpenDegrees : 0;
        if (Mathf.IsEqualApprox(_angle, target)) return;
        var step = OpenDegrees / SwingSeconds * (float)delta;
        SetAngle(Mathf.MoveToward(_angle, target, step));
    }

    private void SetAngle(float degrees)
    {
        _angle = degrees;
        _hinge.RotationDegrees = new Vector3(0, degrees, 0); // +Y turns the panel's +X toward -Z: inward
        _shape.Transform = _hinge.Transform * new Transform3D(Basis.Identity, new Vector3(Width / 2, Height / 2, 0));
    }

    /// <summary>Is a world point on the inside of this door (behind the front wall)?</summary>
    public bool IsInside(Vector3 worldPoint) => ToLocal(worldPoint).Z < -Inset;

    private void UpdatePrompt() =>
        Prompt = IsOpen ? "Close door"
            : IsLocked ? "Locked door  ·  [L] Unlock"
            : "Open door  ·  [L] Lock";
}
