using System.Collections.Generic;
using ExploreTogether.Core.City;
using ExploreTogether.Dungeon;
using Godot;

namespace ExploreTogether.City;

/// <summary>
/// Builds the furniture <see cref="Furniture.Generate"/> lays out, on server and client alike (both need the
/// collision). Fixed pieces (beds, tables, counters, shelves...) are drawn into the city's merged mesh, out of a few
/// boxes each, with one collision box per piece. Chairs and stools are separate nodes (<see cref="CityChair"/>):
/// E picks one up (it becomes an item), so it can vanish when someone takes it.
/// </summary>
public static class CityFurniture
{
    public const string Prefix = "furniture:";

    private static readonly Color DarkWood = new(0.32f, 0.21f, 0.12f), Wood = new(0.50f, 0.34f, 0.19f),
        LightWood = new(0.66f, 0.50f, 0.32f), Linen = new(0.86f, 0.84f, 0.78f), Blanket = new(0.36f, 0.42f, 0.58f),
        Cloth = new(0.45f, 0.20f, 0.18f), Steel = new(0.40f, 0.42f, 0.45f), Crate = new(0.60f, 0.47f, 0.28f);

    public static string IdFor(int piece) => Prefix + piece;

    public static bool TryParse(string interactionId, out int piece)
    {
        piece = -1;
        return interactionId.StartsWith(Prefix) && int.TryParse(interactionId[Prefix.Length..], out piece);
    }

    internal static void Build(CityBuilder.Boxes b, Node3D level, CityLayout city)
    {
        var chairs = new Node3D { Name = "Furniture" };
        level.AddChild(chairs);
        foreach (var f in Furniture.Generate(city))
        {
            if (f.Pickable) chairs.AddChild(CityChair.Create(f));
            else AddPiece(b, f);
        }
    }

    /// <summary>
    /// A fixed piece out of boxes. "Back" is the wall side, "front" the room side; depth runs back to front, from 0
    /// to 1 across the footprint.
    /// </summary>
    private static void AddPiece(CityBuilder.Boxes b, FurniturePiece f)
    {
        var y = f.Y + 0.04f; // on the floor plate
        var r = f.Rect;
        b.Solid(new Vector3(r.MinX, y, r.MinZ), new Vector3(r.MaxX, y + f.Height, r.MaxZ));

        // A slice of the footprint by depth (0 = against the wall, 1 = the front edge) and inset along the wall.
        // a0..a1: the part of the length along the wall it covers (fractions), then inset by alongInset (m).
        void Part(float d0, float d1, float h0, float h1, Color color, float alongInset = 0, float a0 = 0, float a1 = 1)
        {
            var (x0, z0, x1, z1) = (r.MinX, r.MinZ, r.MaxX, r.MaxZ);
            float Lerp(float a, float c, float t) => a + (c - a) * t;
            if (f.FacingX != 0)
            {
                // Facing +X: the back is MinX. Facing -X: the back is MaxX.
                var (back, front) = f.FacingX > 0 ? (x0, x1) : (x1, x0);
                (x0, x1) = (Lerp(back, front, d0), Lerp(back, front, d1));
                (z0, z1) = (Lerp(r.MinZ, r.MaxZ, a0) + alongInset, Lerp(r.MinZ, r.MaxZ, a1) - alongInset);
            }
            else
            {
                var (back, front) = f.FacingZ > 0 ? (z0, z1) : (z1, z0);
                (z0, z1) = (Lerp(back, front, d0), Lerp(back, front, d1));
                (x0, x1) = (Lerp(r.MinX, r.MaxX, a0) + alongInset, Lerp(r.MinX, r.MaxX, a1) - alongInset);
            }
            b.Add(new Vector3(Mathf.Min(x0, x1), y + h0, Mathf.Min(z0, z1)), new Vector3(Mathf.Max(x0, x1), y + h1, Mathf.Max(z0, z1)),
                color, solid: false);
        }

        // Four legs under a top at height h (inset from the corners).
        void Legs(float h, Color color)
        {
            const float leg = 0.06f;
            foreach (var (dx, dz) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
            {
                var x = dx == 0 ? r.MinX + 0.05f : r.MaxX - 0.05f - leg;
                var z = dz == 0 ? r.MinZ + 0.05f : r.MaxZ - 0.05f - leg;
                b.Add(new Vector3(x, y, z), new Vector3(x + leg, y + h, z + leg), color, solid: false);
            }
        }

        switch (f.Kind)
        {
            case FurnitureKind.Bed:
                Part(0, 1, 0.1f, 0.35f, DarkWood);              // frame
                Part(0, 0.06f, 0, 0.95f, DarkWood);             // headboard against the wall
                Part(0.06f, 1, 0.35f, 0.5f, Linen, 0.04f);      // mattress
                Part(0.08f, 0.25f, 0.5f, 0.58f, Linen, 0.12f);  // pillow
                Part(0.35f, 1, 0.5f, 0.53f, Blanket, 0.02f);    // blanket
                Legs(0.1f, DarkWood);
                break;
            case FurnitureKind.Table:
            case FurnitureKind.Desk:
                Part(0, 1, f.Height - 0.05f, f.Height, f.Kind == FurnitureKind.Desk ? DarkWood : Wood);
                if (f.Kind == FurnitureKind.Desk)
                {
                    Part(0, 1, 0, f.Height - 0.05f, DarkWood.Darkened(0.1f), 0, 0, 0.35f);  // drawers at one end
                    Part(0, 1, 0, f.Height - 0.05f, DarkWood.Darkened(0.1f), 0, 0.94f, 1);  // a panel at the other
                    Part(0, 0.06f, 0.3f, f.Height - 0.05f, DarkWood.Darkened(0.25f), 0, 0.35f, 0.94f); // modesty panel
                }
                else Legs(f.Height - 0.05f, Wood.Darkened(0.2f));
                break;
            case FurnitureKind.Counter:
            case FurnitureKind.Bar:
                Part(0, 0.92f, 0, f.Height - 0.05f, f.Kind == FurnitureKind.Bar ? DarkWood : Wood);
                Part(0, 1, f.Height - 0.05f, f.Height, f.Kind == FurnitureKind.Bar ? Wood : LightWood);
                if (f.Kind == FurnitureKind.Bar) Part(0.9f, 0.95f, 0.15f, 0.2f, Steel, 0.05f); // foot rail
                break;
            case FurnitureKind.Shelf:
                Part(0, 0.15f, 0, f.Height, DarkWood);                   // back
                Part(0, 1, 0, f.Height, DarkWood, 0, 0, 0.04f);          // ends
                Part(0, 1, 0, f.Height, DarkWood, 0, 0.96f, 1);
                for (var i = 0; i < 4; i++)
                {
                    var h = i * 0.6f;
                    Part(0.15f, 1, h, h + 0.04f, LightWood, 0, 0.04f, 0.96f);
                    // Something on each shelf: jars, books, boxes, in a few colours.
                    if (i == 3) break;
                    var tint = new[] { Cloth, Blanket, Crate, Linen }[(f.Id + i) % 4];
                    Part(0.25f, 0.9f, h + 0.04f, h + 0.32f, tint, 0, 0.1f + 0.1f * ((f.Id + i) % 3), 0.6f + 0.1f * ((f.Id + i) % 3));
                }
                break;
            case FurnitureKind.Crate:
                Part(0, 1, 0, f.Height, Crate);
                Part(-0.01f, 1.01f, 0.12f, 0.2f, Crate.Darkened(0.35f), -0.01f); // straps
                Part(-0.01f, 1.01f, f.Height - 0.2f, f.Height - 0.12f, Crate.Darkened(0.35f), -0.01f);
                break;
            case FurnitureKind.Wardrobe:
                Part(0, 1, 0, f.Height, DarkWood);
                Part(1, 1.02f, 0.1f, f.Height - 0.1f, Wood, 0.05f); // doors
                break;
            case FurnitureKind.Sofa:
                Part(0, 1, 0.1f, 0.45f, Cloth);                 // seat
                Part(0, 0.28f, 0.45f, f.Height, Cloth.Darkened(0.15f)); // back against the wall
                Part(0, 1, 0, 0.1f, DarkWood);
                break;
        }
    }
}

/// <summary>A chair or stool someone can pick up (E): it becomes an item in their inventory, and vanishes here.</summary>
public partial class CityChair : Interactable
{
    public int PieceId { get; private set; }
    public FurnitureKind Kind { get; private set; }

    private static readonly Dictionary<FurnitureKind, Mesh> Meshes = new();

    public static CityChair Create(FurniturePiece f)
    {
        var chair = new CityChair
        {
            Name = $"Furniture{f.Id}",
            PieceId = f.Id,
            Kind = f.Kind,
            InteractionId = CityFurniture.IdFor(f.Id),
            Prompt = f.Kind == FurnitureKind.Stool ? "Pick up the stool" : "Pick up the chair",
            Position = new Vector3(f.Rect.CenterX, f.Y + 0.04f, f.Rect.CenterZ),
            // The game's facing convention: yaw 0 faces -Z, so facing (fx, fz) is atan2(-fx, -fz).
            Rotation = new Vector3(0, Mathf.Atan2(-f.FacingX, -f.FacingZ), 0),
        };
        chair.AddChild(new MeshInstance3D { Name = "Mesh", Mesh = MeshFor(f.Kind), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        var height = f.Kind == FurnitureKind.Stool ? 0.75f : 0.95f;
        chair.AddChild(new CollisionShape3D
        {
            Name = "Shape",
            Shape = new BoxShape3D { Size = new Vector3(0.44f, height, 0.44f) },
            Position = new Vector3(0, height / 2, 0),
        });
        return chair;
    }

    /// <summary>Taken (or put back, if that's ever added): hidden, and no longer in anyone's way.</summary>
    public void SetTaken(bool taken)
    {
        Visible = !taken;
        CollisionLayer = taken ? 0u : 1u;
        InputRayPickable = !taken;
    }

    /// <summary>One merged mesh per kind (seat, legs, and a back for chairs), built once and shared.</summary>
    private static Mesh MeshFor(FurnitureKind kind)
    {
        if (Meshes.TryGetValue(kind, out var cached)) return cached;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        void Box(Vector3 min, Vector3 max, Color c)
        {
            st.SetColor(c);
            var size = max - min;
            var box = new BoxMesh { Size = size };
            var arrays = box.GetMeshArrays();
            var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
            var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            foreach (var i in indices)
            {
                st.SetNormal(normals[i]);
                st.AddVertex(verts[i] + (min + max) / 2);
            }
        }
        var wood = new Color(0.50f, 0.34f, 0.19f);
        var seatHeight = kind == FurnitureKind.Stool ? 0.72f : 0.45f;
        const float half = 0.2f, leg = 0.035f;
        foreach (var (sx, sz) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
            Box(new Vector3(sx * (half - 0.02f) - leg, 0, sz * (half - 0.02f) - leg), new Vector3(sx * (half - 0.02f) + leg, seatHeight, sz * (half - 0.02f) + leg), wood.Darkened(0.2f));
        Box(new Vector3(-half, seatHeight, -half), new Vector3(half, seatHeight + 0.05f, half),
            kind == FurnitureKind.Stool ? wood : new Color(0.45f, 0.20f, 0.18f));
        if (kind == FurnitureKind.Chair) // the back, on the side away from where it faces (+Z locally)
            Box(new Vector3(-half, seatHeight + 0.05f, half - 0.05f), new Vector3(half, 0.95f, half), wood);
        var material = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.85f };
        st.SetMaterial(material);
        return Meshes[kind] = st.Commit();
    }
}
