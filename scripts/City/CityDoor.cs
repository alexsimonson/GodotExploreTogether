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
/// The deadbolt is a brass lock above the handle, with its own small hitbox (<see cref="CityDeadbolt"/>) so a
/// shotgun slug aimed at it can destroy it (see ServerGame.Breaching). A shot-out lock leaves a splintered hole; a
/// kicked-in door hangs open at a slant, off its top hinge.
///
/// Interaction: E opens or closes it (if it isn't locked). L locks or unlocks it: with a key from outside, or
/// freely from inside; without the key, holding L picks the lock (with a lockpick). K kicks it.
/// </summary>
public partial class CityDoor : Interactable
{
    public const string Prefix = "door:";
    public const float Width = 1.6f, Height = 2.4f;
    private const float Thickness = 0.06f, Inset = 0.15f, OpenDegrees = 90f, SwingSeconds = 0.5f;
    /// <summary>Where the lock sits on the panel (from the hinge), and its height.</summary>
    public const float LockAlong = Width - 0.2f, LockHeight = 1.25f;

    public int ParcelId { get; private set; }
    public bool IsOpen => State.Open;
    public bool IsLocked => State.Locked;
    public CityDoors.DoorState State { get; private set; }

    private Node3D _hinge = null!;
    private Node3D _panel = null!;
    private CollisionShape3D _shape = null!; // a direct child of the body (shapes must be), moved along with the hinge
    private CityDeadbolt _deadbolt = null!;
    private Node3D _hole = null!;
    private float _angle; // current swing, degrees (0 = closed)
    private float _shudder; // seconds left of a kick's shudder

    private static StandardMaterial3D? _wood, _metal, _dark;

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
        _dark ??= new StandardMaterial3D { AlbedoColor = new Color(0.08f, 0.06f, 0.05f), Roughness = 1f };

        door._hinge = new Node3D { Name = "Hinge", Position = new Vector3(-Width / 2, 0, -Inset) };
        door.AddChild(door._hinge);
        door._panel = new Node3D { Name = "PanelPivot" };
        door._hinge.AddChild(door._panel);
        var size = new Vector3(Width - 0.04f, Height - 0.04f, Thickness);
        var center = new Vector3(Width / 2, Height / 2, 0);
        door._panel.AddChild(new MeshInstance3D { Name = "Panel", Mesh = new BoxMesh { Size = size, Material = _wood }, Position = center });
        door._shape = new CollisionShape3D { Name = "Shape", Shape = new BoxShape3D { Size = size } };
        door.AddChild(door._shape);
        foreach (var side in new[] { 1f, -1f })
            door._panel.AddChild(new MeshInstance3D
            {
                Name = side > 0 ? "KnobOut" : "KnobIn",
                Mesh = new SphereMesh { Radius = 0.05f, Height = 0.1f, Material = _metal },
                Position = new Vector3(Width - 0.2f, 1.0f, side * 0.06f),
            });

        // The deadbolt: a brass plate and keyhole cylinder outside, a thumb-turn inside, with its own hitbox.
        door._deadbolt = new CityDeadbolt { Name = "Deadbolt", Door = door, InteractionId = door.InteractionId, Position = new Vector3(LockAlong, LockHeight, 0) };
        door._deadbolt.AddChild(new MeshInstance3D { Name = "Plate", Mesh = new BoxMesh { Size = new Vector3(0.09f, 0.16f, Thickness + 0.03f), Material = _metal } });
        door._deadbolt.AddChild(new MeshInstance3D
        {
            Name = "Cylinder",
            Mesh = new CylinderMesh { TopRadius = 0.028f, BottomRadius = 0.028f, Height = 0.05f, Material = _metal },
            Position = new Vector3(0, 0.02f, 0.045f),
            RotationDegrees = new Vector3(90, 0, 0),
        });
        door._deadbolt.AddChild(new MeshInstance3D
        {
            Name = "Keyhole",
            Mesh = new BoxMesh { Size = new Vector3(0.012f, 0.03f, 0.01f), Material = _dark },
            Position = new Vector3(0, 0.02f, 0.072f),
        });
        door._deadbolt.AddChild(new MeshInstance3D
        {
            Name = "ThumbTurn",
            Mesh = new BoxMesh { Size = new Vector3(0.02f, 0.06f, 0.03f), Material = _metal },
            Position = new Vector3(0, 0.02f, -0.05f),
        });
        door._deadbolt.AddChild(new CollisionShape3D { Name = "Shape", Shape = new BoxShape3D { Size = new Vector3(0.14f, 0.2f, Thickness + 0.1f) } });
        door._panel.AddChild(door._deadbolt);

        // What's left after a slug: a splintered hole where the lock was.
        door._hole = new MeshInstance3D
        {
            Name = "Hole",
            Mesh = new BoxMesh { Size = new Vector3(0.16f, 0.2f, Thickness + 0.01f), Material = _dark },
            Position = new Vector3(LockAlong, LockHeight, 0),
            Visible = false,
        };
        door._panel.AddChild(door._hole);

        door.SetAngle(0);
        door.UpdatePrompt();
        return door;
    }

    /// <summary>Applies the server's state. Clients swing the panel over half a second; the server snaps it.</summary>
    public void SetState(CityDoors.DoorState state, bool animate)
    {
        State = state;
        if (!animate) SetAngle(TargetAngle);
        _deadbolt.Visible = !state.Damaged;
        _deadbolt.InputRayPickable = !state.Damaged;
        _deadbolt.CollisionLayer = state.Damaged ? 0u : 1u;
        _hole.Visible = state.LockBroken;
        // Kicked in: hanging off the top hinge, tilted, and the lock torn out.
        _panel.Rotation = state.Broken ? new Vector3(0, 0, Mathf.DegToRad(-9)) : Vector3.Zero;
        _panel.Position = state.Broken ? new Vector3(0, -0.08f, 0) : Vector3.Zero;
        UpdatePrompt();
    }

    /// <summary>A kick that didn't break it: the panel shudders.</summary>
    public void Shudder() => _shudder = 0.25f;

    private float TargetAngle => State.Broken ? OpenDegrees + 12 : State.Open ? OpenDegrees : 0;

    public override void _Process(double delta)
    {
        var target = TargetAngle;
        if (_shudder > 0)
        {
            _shudder -= (float)delta;
            SetAngle(Mathf.Sin(_shudder * 60f) * 3f * (_shudder / 0.25f));
            return;
        }
        if (Mathf.IsEqualApprox(_angle, target)) return;
        var step = OpenDegrees / SwingSeconds * (float)delta * (State.Broken ? 3 : 1);
        SetAngle(Mathf.MoveToward(_angle, target, step));
    }

    private void SetAngle(float degrees)
    {
        _angle = degrees;
        _hinge.RotationDegrees = new Vector3(0, degrees, 0); // +Y turns the panel's +X toward -Z: inward
        _shape.Transform = _hinge.Transform * _panel.Transform * new Transform3D(Basis.Identity, new Vector3(Width / 2, Height / 2, 0));
    }

    /// <summary>Is a world point on the inside of this door (behind the front wall)?</summary>
    public bool IsInside(Vector3 worldPoint) => ToLocal(worldPoint).Z < -Inset;

    /// <summary>The deadbolt's middle in world space (where a slug has to land).</summary>
    public Vector3 LockPosition => _deadbolt.GlobalPosition;

    private void UpdatePrompt()
    {
        var s = State;
        Prompt = s.Broken ? "Broken door"
            : s.Open ? "Close door"
            : s.Locked ? "Locked door  ·  [L] Unlock / hold to pick  ·  [K] Kick"
            : s.LockBroken ? "Open door (lock shot out)  ·  [K] Kick"
            : "Open door  ·  [L] Lock  ·  [K] Kick";
        if (_deadbolt != null) _deadbolt.Prompt = Prompt;
    }
}

/// <summary>A door's deadbolt: its own hitbox (a shotgun slug through it destroys the lock); E on it is E on the door.</summary>
public partial class CityDeadbolt : Interactable
{
    public CityDoor Door { get; set; } = null!;
}
