using ExploreTogether.Core.Items;
using Godot;

namespace ExploreTogether;

/// <summary>
/// A placeholder shape for a weapon held in someone's hand (players seen by others, residents with a weapon drawn),
/// so a passer-by can tell an empty hand from a pistol or a shotgun. Points along -Z (forward).
/// </summary>
public static class WeaponLook
{
    private static readonly StandardMaterial3D Steel = new() { AlbedoColor = new Color(0.18f, 0.18f, 0.2f), Metallic = 0.7f, Roughness = 0.4f };
    private static readonly StandardMaterial3D Wood = new() { AlbedoColor = new Color(0.45f, 0.3f, 0.16f), Roughness = 0.8f };
    private static readonly StandardMaterial3D Stone = new() { AlbedoColor = new Color(0.6f, 0.6f, 0.58f), Roughness = 0.95f };
    private static readonly StandardMaterial3D Glow = new() { AlbedoColor = new Color(0.5f, 0.3f, 0.8f), EmissionEnabled = true, Emission = new Color(0.4f, 0.2f, 0.7f) };

    public static MeshInstance3D Create(ItemDef def)
    {
        var (size, material) = def switch
        {
            { Id: ItemCatalog.ConcreteBlockId } => (new Vector3(0.2f, 0.1f, 0.4f), Stone),
            { Class: ItemClass.Gun, Breaches: true } => (new Vector3(0.07f, 0.09f, 0.8f), Steel),
            { Class: ItemClass.Gun } => (new Vector3(0.05f, 0.12f, 0.22f), Steel),
            { Class: ItemClass.Magic } => (new Vector3(0.04f, 0.04f, 0.5f), Glow),
            { KnocksOut: true } => (new Vector3(0.07f, 0.07f, 0.8f), Wood),
            _ => (new Vector3(0.05f, 0.05f, 0.85f), def.Class == ItemClass.Tool ? Wood : Steel),
        };
        return new MeshInstance3D
        {
            Name = "Weapon",
            Mesh = new BoxMesh { Size = size, Material = material },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }
}
