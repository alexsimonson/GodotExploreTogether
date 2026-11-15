using System.Collections.Generic;
using Godot;

namespace ExploreTogether.Client;

/// <summary>
/// Client-side debug toggle for before/after material comparison (F1 switches).
/// Full (the default) is the materials as authored. Flat turns off normal maps,
/// height maps, gloss and metallic, leaving color only.
/// Changes are made on the shared material resources, so every mesh using a material
/// changes together. Original values are remembered so Full restores them exactly.
/// Only textured materials are affected (player bodies etc. are left alone).
/// </summary>
public static class MaterialDebug
{
    public enum Mode
    {
        Full, // everything enabled, as authored
        Flat, // everything disabled: color only, matte
    }

    private sealed record Original(bool Normal, bool Height, float Roughness, float Metallic);

    private static readonly Dictionary<BaseMaterial3D, Original> Originals = new();

    public static Mode Current { get; private set; } = Mode.Full;

    public static string Describe(Mode mode) => mode switch
    {
        Mode.Full => "ALL effects ON (normal + height maps, gloss)",
        Mode.Flat => "ALL effects OFF (color only)",
        _ => mode.ToString(),
    };

    public static void Toggle(Node root)
    {
        Current = Current == Mode.Full ? Mode.Flat : Mode.Full;
        Apply(root);
    }

    /// <summary>Applies the current mode to every textured material under root.</summary>
    public static void Apply(Node root)
    {
        var full = Current == Mode.Full;
        foreach (var material in Collect(root))
        {
            if (!Originals.TryGetValue(material, out var o))
            {
                o = new Original(material.NormalEnabled, material.HeightmapEnabled, material.Roughness, material.Metallic);
                Originals[material] = o;
            }

            material.NormalEnabled = full && o.Normal;
            material.HeightmapEnabled = full && o.Height;
            material.Roughness = full ? o.Roughness : 1f;
            material.Metallic = full ? o.Metallic : 0f;
        }
    }

    private static HashSet<BaseMaterial3D> Collect(Node root)
    {
        var found = new HashSet<BaseMaterial3D>();
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            foreach (var child in node.GetChildren()) stack.Push(child);
            if (node is not MeshInstance3D mi) continue;

            Add(mi.MaterialOverride);
            if (mi.Mesh == null) continue;
            for (int i = 0; i < mi.Mesh.GetSurfaceCount(); i++)
            {
                Add(mi.GetSurfaceOverrideMaterial(i));
                Add(mi.Mesh.SurfaceGetMaterial(i));
            }
        }
        return found;

        void Add(Material? m)
        {
            if (m is BaseMaterial3D b && b.AlbedoTexture != null) found.Add(b);
        }
    }
}
