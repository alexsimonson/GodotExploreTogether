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
/// shotgun slug aimed at it can destroy it (see ServerGame.Breaching). A shot-out lock leaves a ragged hole with
/// splinters of raw wood, scorch marks and a drooping handle, and the door can't latch any more: it swings ajar
/// (<see cref="AjarDegrees"/>) and drifts back there if closed. A kicked-in door hangs open at a slant, off its top
/// hinge.
///
/// Interaction: E opens or closes it (if it isn't locked). L locks or unlocks it: with a key from outside, or
/// freely from inside; without the key, holding L picks the lock (with a lockpick). K kicks it.
/// </summary>
public partial class CityDoor : Interactable
{
    public const string Prefix = "door:";
    public const float Width = 1.6f, Height = 2.4f;
    private const float Thickness = 0.06f, Inset = 0.15f, OpenDegrees = 90f, SwingSeconds = 0.5f;
    /// <summary>How far a door with its lock shot out stands open when nobody has opened it (it can't latch).</summary>
    public const float AjarDegrees = 12f;
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
    private Node3D _knobOut = null!;
    private float _angle; // current swing, degrees (0 = closed)
    private float _shudder; // seconds left of a kick's shudder

    private static StandardMaterial3D? _wood, _metal, _dark, _rawWood, _scorch;

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
        {
            var knob = new MeshInstance3D
            {
                Name = side > 0 ? "KnobOut" : "KnobIn",
                Mesh = new SphereMesh { Radius = 0.05f, Height = 0.1f, Material = _metal },
                Position = new Vector3(Width - 0.2f, 1.0f, side * 0.06f),
            };
            door._panel.AddChild(knob);
            if (side > 0) door._knobOut = knob;
        }

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

        // What's left after a slug: a ragged hole where the lock was, splinters, scorch marks (see BuildDamage).
        door._hole = BuildDamage(p.Id);
        door._panel.AddChild(door._hole);

        door.SetAngle(0);
        door.UpdatePrompt();
        return door;
    }

    /// <summary>
    /// The slug damage around the lock (hidden until the lock is shot out). Laid out from the parcel id, so it looks
    /// the same for everyone.
    /// </summary>
    private static Node3D BuildDamage(int seed)
    {
        _rawWood ??= new StandardMaterial3D { AlbedoColor = new Color(0.78f, 0.62f, 0.42f), Roughness = 0.9f };
        _scorch ??= new StandardMaterial3D
        {
            AlbedoColor = new Color(0.05f, 0.04f, 0.03f, 0.75f), Roughness = 1f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        };
        var random = new RandomNumberGenerator { Seed = (ulong)(seed * 7919 + 17) };
        var damage = new Node3D { Name = "Hole", Position = new Vector3(LockAlong, LockHeight, 0), Visible = false };
        // The hole: a few overlapping dark blocks, so its edge is ragged instead of square.
        for (var i = 0; i < 4; i++)
            damage.AddChild(new MeshInstance3D
            {
                Name = $"Hole{i}",
                Mesh = new BoxMesh { Size = new Vector3(random.RandfRange(0.07f, 0.13f), random.RandfRange(0.08f, 0.15f), Thickness + 0.012f), Material = _dark },
                Position = new Vector3(random.RandfRange(-0.03f, 0.03f), random.RandfRange(-0.04f, 0.04f), 0),
                RotationDegrees = new Vector3(0, 0, random.RandfRange(-30, 30)),
            });
        // Scorch and pellet marks on both faces, spreading out from the hole.
        foreach (var face in new[] { 1f, -1f })
        {
            damage.AddChild(new MeshInstance3D
            {
                Name = face > 0 ? "ScorchOut" : "ScorchIn",
                Mesh = new CylinderMesh { TopRadius = 0.15f, BottomRadius = 0.15f, Height = 0.002f, Material = _scorch },
                Position = new Vector3(0, 0, face * (Thickness / 2 + 0.002f)),
                RotationDegrees = new Vector3(90, 0, 0),
                Scale = new Vector3(1, 1, face > 0 ? 1.15f : 0.9f),
            });
            for (var i = 0; i < 7; i++)
            {
                var a = random.RandfRange(0, Mathf.Tau);
                var r = random.RandfRange(0.1f, 0.24f);
                damage.AddChild(new MeshInstance3D
                {
                    Name = $"Pellet{(face > 0 ? "Out" : "In")}{i}",
                    Mesh = new BoxMesh { Size = new Vector3(0.018f, 0.018f, 0.004f), Material = _dark },
                    Position = new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, face * (Thickness / 2 + 0.003f)),
                });
            }
        }
        // Splinters of raw wood bursting out of the inside face (the slug goes in from the street), and a few outside.
        for (var i = 0; i < 10; i++)
        {
            var a = random.RandfRange(0, Mathf.Tau);
            var r = random.RandfRange(0.05f, 0.09f);
            var face = i < 7 ? -1f : 1f;
            damage.AddChild(new MeshInstance3D
            {
                Name = $"Splinter{i}",
                Mesh = new BoxMesh { Size = new Vector3(0.012f, random.RandfRange(0.05f, 0.11f), 0.01f), Material = _rawWood },
                Position = new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, face * (Thickness / 2 + 0.02f)),
                // Pointing outward from the hole, bent away from the face.
                Rotation = new Vector3(face * random.RandfRange(0.4f, 1.0f), 0, a - Mathf.Pi / 2),
            });
        }
        return damage;
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
        // The slug went through the lock just above the handle: it hangs loose.
        _knobOut.RotationDegrees = state.LockBroken ? new Vector3(25, 0, -35) : Vector3.Zero;
        _knobOut.Position = new Vector3(Width - 0.2f, state.LockBroken ? 0.96f : 1.0f, 0.06f);
        // Kicked in: hanging off the top hinge, tilted, and the lock torn out.
        _panel.Rotation = state.Broken ? new Vector3(0, 0, Mathf.DegToRad(-9)) : Vector3.Zero;
        _panel.Position = state.Broken ? new Vector3(0, -0.08f, 0) : Vector3.Zero;
        UpdatePrompt();
    }

    /// <summary>A kick that didn't break it: the panel shudders.</summary>
    public void Shudder() => _shudder = 0.25f;

    private float TargetAngle => State.Broken ? OpenDegrees + 12 : State.Open ? OpenDegrees : State.LockBroken ? AjarDegrees : 0;

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
        // Kicked in, or blown ajar by the slug: fast.
        var fast = State.Broken || State.LockBroken && !State.Open && _angle < AjarDegrees;
        var step = OpenDegrees / SwingSeconds * (float)delta * (fast ? 4 : 1);
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
            : s.LockBroken ? "Open door (ajar, lock shot out)  ·  [K] Kick"
            : "Open door  ·  [L] Lock  ·  [K] Kick";
        if (_deadbolt != null) _deadbolt.Prompt = Prompt;
    }
}

/// <summary>A door's deadbolt: its own hitbox (a shotgun slug through it destroys the lock); E on it is E on the door.</summary>
public partial class CityDeadbolt : Interactable
{
    public CityDoor Door { get; set; } = null!;
}
