using System.Collections.Generic;
using ExploreTogether.Core.City;
using Godot;

namespace ExploreTogether.City;

/// <summary>
/// The root script of a custom building scene in res://scenes/city/buildings/ (see docs/CITY-BUILDINGS.md).
/// It tells the city generator what the building is and how big; the scene itself holds the meshes and collision.
///
/// Conventions (local space): the origin is the middle of the front edge on the ground, the front faces +Z (toward
/// the street), and the building covers x in [-Width/2, Width/2], z in [-Depth, 0]. The doorway is centered at
/// x = DoorOffset on the front face. Optional Marker3D children under a node named "Spots" are where people stand.
///
/// In the editor, it draws the footprint (green), the doorway (yellow) and an arrow toward the street, so the
/// model can be lined up with what the generator will assume.
/// </summary>
[Tool, GlobalClass]
public partial class CityBuilding : Node3D
{
    private BuildingType _type = BuildingType.House;
    private float _width = 8, _depth = 8, _doorOffset;
    private int _floors = 1, _weight = BuildingCatalog.DefaultWeight;

    /// <summary>Who uses it: homes (Cottage, House, RowHouse, Manor), workplaces (Shop, Office, Warehouse), Guardhouse.</summary>
    [Export] public BuildingType Type { get => _type; set { _type = value; UpdatePreview(); } }
    /// <summary>Meters along the street (local X).</summary>
    [Export(PropertyHint.Range, "3,34,0.1")] public float Width { get => _width; set { _width = value; UpdatePreview(); } }
    /// <summary>Meters back from the street (local -Z).</summary>
    [Export(PropertyHint.Range, "3,22,0.1")] public float Depth { get => _depth; set { _depth = value; UpdatePreview(); } }
    [Export(PropertyHint.Range, "1,6,1")] public int Floors { get => _floors; set { _floors = value; UpdatePreview(); } }
    /// <summary>Where the doorway's middle is along the front (local X).</summary>
    [Export] public float DoorOffset { get => _doorOffset; set { _doorOffset = value; UpdatePreview(); } }
    /// <summary>How often it's picked among the designs of its type that fit (each greybox design has 10; 0 = never).</summary>
    [Export(PropertyHint.Range, "0,1000,1")] public int Weight { get => _weight; set => _weight = value; }

    public const string SpotsNodeName = "Spots";

    /// <summary>The generator's view of this building. The name is the scene's file name.</summary>
    public BuildingVariant ToVariant(string name)
    {
        var spots = new List<(float, float)>();
        if (GetNodeOrNull(SpotsNodeName) is Node3D spotsNode)
            foreach (var child in spotsNode.GetChildren())
                if (child is Node3D marker)
                {
                    var p = spotsNode.Transform * marker.Position;
                    spots.Add((p.X, p.Z));
                }
        return new BuildingVariant(name, Type, Width, Depth, Floors, DoorOffset, Weight) { Spots = spots };
    }

    // ---- editor preview ----

    private MeshInstance3D? _preview;

    public override void _Ready()
    {
        if (Engine.IsEditorHint()) UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (!Engine.IsEditorHint() || !IsInsideTree()) return;
        if (_preview == null)
        {
            _preview = new MeshInstance3D
            {
                Name = "_FootprintPreview",
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    VertexColorUseAsAlbedo = true,
                    NoDepthTest = true,
                },
            };
            AddChild(_preview); // not owned by the scene, so it's never saved
        }
        var mesh = new ImmediateMesh();
        mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        void Line(Vector3 a, Vector3 b, Color c)
        {
            mesh.SurfaceSetColor(c);
            mesh.SurfaceAddVertex(a);
            mesh.SurfaceSetColor(c);
            mesh.SurfaceAddVertex(b);
        }
        const float y = 0.05f;
        float hw = Width / 2, top = Floors * CityBuilder.FloorHeight;
        var green = new Color(0.2f, 1f, 0.3f);
        Vector3[] corners = [new(-hw, y, 0), new(hw, y, 0), new(hw, y, -Depth), new(-hw, y, -Depth)];
        for (var i = 0; i < 4; i++)
        {
            Line(corners[i], corners[(i + 1) % 4], green);
            Line(corners[i], corners[i] with { Y = top }, green * 0.6f); // height of the floors, for scale
        }
        var yellow = new Color(1f, 0.85f, 0.1f);
        Line(new Vector3(DoorOffset - 0.8f, y, 0), new Vector3(DoorOffset - 0.8f, 2.4f, 0), yellow);
        Line(new Vector3(DoorOffset + 0.8f, y, 0), new Vector3(DoorOffset + 0.8f, 2.4f, 0), yellow);
        Line(new Vector3(DoorOffset - 0.8f, 2.4f, 0), new Vector3(DoorOffset + 0.8f, 2.4f, 0), yellow);
        // Arrow toward the street (+Z).
        var cyan = new Color(0.2f, 0.9f, 1f);
        Line(new Vector3(DoorOffset, y, 0.2f), new Vector3(DoorOffset, y, 2.5f), cyan);
        Line(new Vector3(DoorOffset, y, 2.5f), new Vector3(DoorOffset - 0.5f, y, 1.9f), cyan);
        Line(new Vector3(DoorOffset, y, 2.5f), new Vector3(DoorOffset + 0.5f, y, 1.9f), cyan);
        mesh.SurfaceEnd();
        _preview.Mesh = mesh;
    }
}
