using ExploreTogether.Core.City;
using ExploreTogether.Core.Dungeon;
using ExploreTogether.Dungeon;
using Godot;

namespace ExploreTogether.City;

/// <summary>
/// The things in a city that work its lights (see <see cref="CityPower"/>), built with the city on server and client:
/// - a fuse box on every building's front wall (<see cref="CityFuseBox"/>): locked; hit or shoot it open, then E cuts
///   or restores the building's power;
/// - a light switch inside every front door (<see cref="CityLightSwitch"/>);
/// - a substation cabinet in each district (<see cref="CitySubstation"/>), and the main breaker inside the power
///   station (<see cref="CityBreaker"/>): E cuts or restores a district, or the whole city;
/// - hitboxes on every street lamp, porch light and ceiling lamp (<see cref="CityLightTarget"/>): shoot them out, or
///   hit them where they're in reach.
/// <see cref="Apply"/> shows the server's state (open boxes, broken lamps, dark districts) on the client.
/// </summary>
public static class CityPowerProps
{
    public const string FuseBoxPrefix = "fusebox:", SwitchPrefix = "switch:", SubstationPrefix = "substation:", BreakerId = "breaker:main";

    private static readonly StandardMaterial3D Metal = new() { AlbedoColor = new Color(0.45f, 0.47f, 0.48f), Metallic = 0.6f, Roughness = 0.5f };
    private static readonly StandardMaterial3D Cabinet = new() { AlbedoColor = new Color(0.32f, 0.40f, 0.33f), Metallic = 0.4f, Roughness = 0.6f };
    private static readonly StandardMaterial3D Warning = new() { AlbedoColor = new Color(0.95f, 0.75f, 0.1f), Roughness = 0.7f };
    private static readonly StandardMaterial3D Plastic = new() { AlbedoColor = new Color(0.9f, 0.88f, 0.82f), Roughness = 0.6f };
    private static readonly StandardMaterial3D Fixture = new()
    {
        AlbedoColor = new Color(1f, 0.9f, 0.7f), EmissionEnabled = true, Emission = new Color(1f, 0.82f, 0.5f),
        EmissionEnergyMultiplier = 1.2f,
    };

    /// <summary>Unit vectors for a parcel: out of the front (toward the street), and along the front (local +X).</summary>
    private static ((float X, float Z) Out, (float X, float Z) Along) Axes(Parcel p)
    {
        var (ox, oz) = MazePieces.Vector(p.Front);
        return ((ox, oz), (oz, -ox));
    }

    /// <summary>Where a building's fuse box hangs: on the front wall beside the door, on whichever side has room.</summary>
    public static Vector3 FuseBoxPosition(Parcel p)
    {
        var (o, a) = Axes(p);
        var side = 1f;
        var probe = (X: p.Door.X + a.X * 1.6f, Z: p.Door.Z + a.Z * 1.6f);
        if (!p.Footprint.Contains(probe.X - o.X * 0.5f, probe.Z - o.Z * 0.5f)) side = -1;
        return new Vector3(p.Door.X + a.X * 1.3f * side + o.X * 0.09f, 1.45f, p.Door.Z + a.Z * 1.3f * side + o.Z * 0.09f);
    }

    /// <summary>Where the light switch is: inside, on the front wall, beside the door (the other side from the fuse box).</summary>
    public static Vector3 SwitchPosition(Parcel p)
    {
        var (o, a) = Axes(p);
        var box = FuseBoxPosition(p);
        var side = (box.X - p.Door.X) * a.X + (box.Z - p.Door.Z) * a.Z > 0 ? -1f : 1f;
        var inset = BuildingInterior.WallThickness + 0.02f;
        return new Vector3(p.Door.X + a.X * 1.05f * side - o.X * inset, 1.3f, p.Door.Z + a.Z * 1.05f * side - o.Z * inset);
    }

    public static void Build(Node3D level, CityLayout city, CityLighting lighting, CityPower power)
    {
        var props = new Node3D { Name = "Power" };
        level.AddChild(props);
        foreach (var p in city.Parcels)
        {
            var yaw = p.Yaw;
            props.AddChild(CityFuseBox.Create(p, FuseBoxPosition(p), yaw));
            props.AddChild(CityLightSwitch.Create(p, SwitchPosition(p), yaw));
            // Ceiling lamps: one per floor people can reach, mid-ceiling.
            var floors = p.Interior?.Floors ?? 1;
            var f = p.Footprint;
            for (var floor = 0; floor < floors; floor++)
                props.AddChild(CityLightTarget.Create(CityLightTarget.Kinds.Ceiling, p.Id,
                    new Vector3(f.CenterX, floor * BuildingInterior.FloorHeight + BuildingInterior.FloorHeight - BuildingInterior.SlabThickness - 0.06f, f.CenterZ),
                    new Vector3(0.45f, 0.1f, 0.45f), floor));
        }
        for (var i = 0; i < lighting.Lamps.Count; i++)
        {
            var (x, z) = lighting.Lamps[i];
            props.AddChild(CityLightTarget.Create(CityLightTarget.Kinds.Lamp, i, new Vector3(x, CityLighting.LampHeight + 0.12f, z), new Vector3(0.5f, 0.35f, 0.5f)));
        }
        for (var i = 0; i < lighting.Porches.Count; i++)
        {
            var p = city.Parcels[lighting.Porches[i].Parcel];
            var (ox, oz) = MazePieces.Vector(p.Front);
            props.AddChild(CityLightTarget.Create(CityLightTarget.Kinds.Porch, i,
                new Vector3(p.Door.X + ox * 0.08f, CityLighting.PorchHeight, p.Door.Z + oz * 0.08f), new Vector3(0.32f, 0.32f, 0.32f)));
        }
        for (var d = 0; d < power.Districts; d++)
            if (power.Substations[d] is { } at)
                props.AddChild(CitySubstation.Create(d, new Vector3(at.X, 0, at.Z)));
        if (power.Station >= 0)
        {
            // The main breaker: on the back wall inside the station, opposite the door.
            var p = city.Parcels[power.Station];
            var (o, _) = Axes(p);
            var f = p.Footprint;
            var depth = p.Front is Sides.North or Sides.South ? f.Depth : f.Width;
            var back = depth - BuildingInterior.WallThickness - 0.1f;
            props.AddChild(CityBreaker.Create(new Vector3(f.CenterX - o.X * (back - depth / 2), 1.4f, f.CenterZ - o.Z * (back - depth / 2)), p.Yaw));
        }
    }

    /// <summary>Shows the server's state: open or cut boxes, broken and unpowered lights, dead districts.</summary>
    public static void Apply(Node3D level, CityLayout city, CityLighting lighting, CityPower power, float hour)
    {
        if (level.GetNodeOrNull<Node3D>("Power") is not { } props) return;
        foreach (var child in props.GetChildren())
            switch (child)
            {
                case CityFuseBox box: box.Show(power.BoxState(box.ParcelId), power.Powered(box.ParcelId)); break;
                case CityLightSwitch sw: sw.Show(power.SwitchState(sw.ParcelId)); break;
                case CitySubstation sub: sub.Show(power.SubstationDown(sub.District)); break;
                case CityBreaker breaker: breaker.Show(power.StationDown); break;
                case CityLightTarget t:
                    t.SetBroken(t.Kind switch
                    {
                        CityLightTarget.Kinds.Lamp => power.LampBroken(t.Index),
                        CityLightTarget.Kinds.Porch => power.PorchBroken(t.Index),
                        _ => power.CeilingBroken(t.Index),
                    });
                    break;
            }
        // The lamps themselves (built by CityBuilder): off if broken or without power.
        if (level.GetNodeOrNull<Node3D>("Lamps") is { } lamps)
        {
            for (var i = 0; i < lighting.Lamps.Count; i++)
                SetVisible(lamps, $"Lamp{i}", !power.LampBroken(i) && power.LampLit(i, CityLighting.LampsOn(hour) ? hour : 0));
            for (var i = 0; i < lighting.Porches.Count; i++)
                SetVisible(lamps, $"Porch{i}", !power.PorchBroken(i) && power.Powered(lighting.Porches[i].Parcel));
        }
    }

    private static void SetVisible(Node3D parent, string prefix, bool on)
    {
        if (parent.GetNodeOrNull<Node3D>(prefix + "Light") is { } light) light.Visible = on;
        // The glass stays; its glow goes with the light.
        if (parent.GetNodeOrNull<MeshInstance3D>(prefix + "Mesh") is { } mesh) mesh.MaterialOverride = on ? null : DeadGlass;
    }

    private static readonly StandardMaterial3D DeadGlass = new() { AlbedoColor = new Color(0.25f, 0.24f, 0.22f), Roughness = 0.4f };

    internal static MeshInstance3D Box(string name, Vector3 size, Material material, Vector3 position) => new()
    {
        Name = name, Mesh = new BoxMesh { Size = size, Material = material }, Position = position,
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
    };

    internal static StandardMaterial3D MetalMaterial => Metal;
    internal static StandardMaterial3D CabinetMaterial => Cabinet;
    internal static StandardMaterial3D WarningMaterial => Warning;
    internal static StandardMaterial3D PlasticMaterial => Plastic;
    internal static StandardMaterial3D FixtureMaterial => Fixture;
}

/// <summary>A building's fuse box, on its front wall: locked (break it open), then E flips its breaker.</summary>
public partial class CityFuseBox : Interactable
{
    public int ParcelId { get; private set; }
    private Node3D _lid = null!;
    private MeshInstance3D _lamp = null!;

    private static readonly StandardMaterial3D Red = new() { AlbedoColor = new Color(0.9f, 0.1f, 0.1f), EmissionEnabled = true, Emission = new Color(0.9f, 0.1f, 0.1f) };
    private static readonly StandardMaterial3D Green = new() { AlbedoColor = new Color(0.1f, 0.8f, 0.2f), EmissionEnabled = true, Emission = new Color(0.1f, 0.7f, 0.2f) };

    public static string IdFor(int parcel) => CityPowerProps.FuseBoxPrefix + parcel;

    public static CityFuseBox Create(Parcel p, Vector3 at, float yaw)
    {
        var box = new CityFuseBox { Name = $"FuseBox{p.Id}", ParcelId = p.Id, InteractionId = IdFor(p.Id), Position = at, Rotation = new Vector3(0, yaw, 0) };
        box.AddChild(CityPowerProps.Box("Body", new Vector3(0.36f, 0.46f, 0.12f), CityPowerProps.MetalMaterial, Vector3.Zero));
        box.AddChild(CityPowerProps.Box("Stripe", new Vector3(0.3f, 0.05f, 0.01f), CityPowerProps.WarningMaterial, new Vector3(0, 0.17f, 0.065f)));
        // The lid hinges on its left edge (seen from the street): swung open once the box is broken into.
        box._lid = new Node3D { Name = "LidHinge", Position = new Vector3(-0.18f, 0, 0.065f) };
        box._lid.AddChild(CityPowerProps.Box("Lid", new Vector3(0.36f, 0.44f, 0.015f), CityPowerProps.MetalMaterial, new Vector3(0.18f, 0, 0)));
        box.AddChild(box._lid);
        box._lamp = CityPowerProps.Box("Indicator", new Vector3(0.04f, 0.04f, 0.02f), Green, new Vector3(0, -0.1f, 0.05f));
        box.AddChild(box._lamp);
        box.AddChild(new CollisionShape3D { Name = "Shape", Shape = new BoxShape3D { Size = new Vector3(0.38f, 0.48f, 0.16f) } });
        box.Show(CityPower.Box.Locked, true);
        return box;
    }

    public void Show(CityPower.Box state, bool powered)
    {
        _lid.RotationDegrees = new Vector3(0, state == CityPower.Box.Locked ? 0 : -110, 0);
        _lamp.MaterialOverride = powered ? Green : Red;
        Prompt = state switch
        {
            CityPower.Box.Locked => "Fuse box (locked: break it open)",
            CityPower.Box.Cut => "Fuse box: restore the power",
            _ => "Fuse box: cut the power",
        };
    }
}

/// <summary>A building's light switch, inside the front door.</summary>
public partial class CityLightSwitch : Interactable
{
    public int ParcelId { get; private set; }
    private MeshInstance3D _toggle = null!;

    public static string IdFor(int parcel) => CityPowerProps.SwitchPrefix + parcel;

    public static CityLightSwitch Create(Parcel p, Vector3 at, float yaw)
    {
        // Facing into the building: turned half round from the front.
        var sw = new CityLightSwitch { Name = $"Switch{p.Id}", ParcelId = p.Id, InteractionId = IdFor(p.Id), Prompt = "Light switch", Position = at, Rotation = new Vector3(0, yaw + Mathf.Pi, 0) };
        sw.AddChild(CityPowerProps.Box("Plate", new Vector3(0.09f, 0.13f, 0.015f), CityPowerProps.PlasticMaterial, Vector3.Zero));
        sw._toggle = CityPowerProps.Box("Toggle", new Vector3(0.025f, 0.045f, 0.02f), CityPowerProps.PlasticMaterial, new Vector3(0, 0.012f, 0.015f));
        sw.AddChild(sw._toggle);
        sw.AddChild(new CollisionShape3D { Name = "Shape", Shape = new BoxShape3D { Size = new Vector3(0.14f, 0.18f, 0.06f) } });
        return sw;
    }

    public void Show(LightSwitch state) =>
        _toggle.Position = new Vector3(0, state == LightSwitch.Off ? -0.012f : 0.012f, 0.015f);
}

/// <summary>A district's substation: a fenced cabinet on a street corner. E cuts (or restores) the district's power.</summary>
public partial class CitySubstation : Interactable
{
    public int District { get; private set; }

    public static string IdFor(int district) => CityPowerProps.SubstationPrefix + district;

    public static CitySubstation Create(int district, Vector3 at)
    {
        var s = new CitySubstation { Name = $"Substation{district}", District = district, InteractionId = IdFor(district), Position = at };
        s.AddChild(CityPowerProps.Box("Cabinet", new Vector3(1.2f, 1.6f, 0.8f), CityPowerProps.CabinetMaterial, new Vector3(0, 0.8f, 0)));
        s.AddChild(CityPowerProps.Box("Plinth", new Vector3(1.4f, 0.12f, 1.0f), CityPowerProps.MetalMaterial, new Vector3(0, 0.06f, 0)));
        foreach (var side in new[] { 1f, -1f })
        {
            s.AddChild(CityPowerProps.Box(side > 0 ? "SignA" : "SignB", new Vector3(0.3f, 0.3f, 0.01f), CityPowerProps.WarningMaterial, new Vector3(0, 1.2f, side * 0.41f)));
            s.AddChild(CityPowerProps.Box(side > 0 ? "SignC" : "SignD", new Vector3(0.01f, 0.3f, 0.3f), CityPowerProps.WarningMaterial, new Vector3(side * 0.61f, 1.2f, 0)));
        }
        s.AddChild(new CollisionShape3D { Name = "Shape", Shape = new BoxShape3D { Size = new Vector3(1.2f, 1.6f, 0.8f) }, Position = new Vector3(0, 0.8f, 0) });
        s.Show(false);
        return s;
    }

    public void Show(bool down) => Prompt = down ? "Substation (cut): restore the district's power" : "Substation: cut the district's power";
}

/// <summary>The power station's main breaker. E cuts (or restores) the power of the whole city.</summary>
public partial class CityBreaker : Interactable
{
    private Node3D _lever = null!;

    public static CityBreaker Create(Vector3 at, float yaw)
    {
        var b = new CityBreaker { Name = "MainBreaker", InteractionId = CityPowerProps.BreakerId, Position = at, Rotation = new Vector3(0, yaw, 0) };
        b.AddChild(CityPowerProps.Box("Panel", new Vector3(0.9f, 1.1f, 0.18f), CityPowerProps.CabinetMaterial, Vector3.Zero));
        b.AddChild(CityPowerProps.Box("Sign", new Vector3(0.5f, 0.12f, 0.01f), CityPowerProps.WarningMaterial, new Vector3(0, 0.42f, 0.095f)));
        b._lever = new Node3D { Name = "LeverPivot", Position = new Vector3(0.2f, 0, 0.1f) };
        b._lever.AddChild(CityPowerProps.Box("Lever", new Vector3(0.06f, 0.35f, 0.06f), CityPowerProps.MetalMaterial, new Vector3(0, 0.17f, 0.03f)));
        b.AddChild(b._lever);
        b.AddChild(new CollisionShape3D { Name = "Shape", Shape = new BoxShape3D { Size = new Vector3(0.9f, 1.1f, 0.3f) } });
        b.Show(false);
        return b;
    }

    public void Show(bool down)
    {
        _lever.RotationDegrees = new Vector3(down ? 150 : 0, 0, 0);
        Prompt = down ? "Main breaker (off): restore the city's power" : "Main breaker: cut the city's power";
    }
}

/// <summary>A light that can be broken (shot, or hit): a street lamp's head, a porch light, or a ceiling lamp.</summary>
public partial class CityLightTarget : StaticBody3D
{
    public enum Kinds { Lamp, Porch, Ceiling }

    public Kinds Kind { get; private set; }
    /// <summary>The lamp or porch index, or the building (parcel) for a ceiling lamp.</summary>
    public int Index { get; private set; }
    private MeshInstance3D? _fixture;

    public static CityLightTarget Create(Kinds kind, int index, Vector3 at, Vector3 size, int floor = 0)
    {
        var t = new CityLightTarget { Name = $"{kind}Target{index}_{floor}", Kind = kind, Index = index, Position = at };
        if (kind == Kinds.Ceiling)
        {
            // The ceiling lamp itself (street lamps and porch lights are drawn by CityBuilder).
            t._fixture = CityPowerProps.Box("Fixture", new Vector3(0.4f, 0.08f, 0.4f), CityPowerProps.FixtureMaterial, Vector3.Zero);
            t.AddChild(t._fixture);
        }
        t.AddChild(new CollisionShape3D { Name = "Shape", Shape = new BoxShape3D { Size = size } });
        return t;
    }

    public void SetBroken(bool broken)
    {
        CollisionLayer = broken ? 0u : 1u;
        if (_fixture != null) _fixture.Scale = broken ? new Vector3(1, 0.4f, 1) : Vector3.One; // shattered: a stub left
    }
}
