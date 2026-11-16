using ExploreTogether.Core.Gathering;
using ExploreTogether.Dungeon;
using Godot;

namespace ExploreTogether.Gathering;

/// <summary>
/// A gatherable resource in an instance (port of the original Resource component on TreeResource.prefab).
/// Spawned by the server through the instance's ResourceSpawner and replicated per instance, like items.
/// Gathering is an interaction ("resource:&lt;node name&gt;"): press E, or hit it with the tool, as in the
/// original (Tool.Attack used whatever interactable it hit). The server rolls and decides
/// (Core.Gathering.ResourceRules); clients only see whether it's depleted.
///
/// Look: a placeholder tree (the original was a plain cube that swapped a green material for a gray one
/// while depleted). Here the canopy does the same.
/// </summary>
public partial class ResourceNode : Interactable
{
    public const string InteractionPrefix = "resource:";

    private static readonly Color ActiveColor = new(0.25f, 0.55f, 0.2f);   // the original GreenMaterial
    private static readonly Color DepletedColor = new(0.45f, 0.45f, 0.45f); // and Gray

    /// <summary>Which ResourceDef this is (ResourceCatalog kind, e.g. "tree").</summary>
    [Export] public string Kind { get; set; } = ResourceCatalog.Tree.Kind;

    private bool _depleted;

    /// <summary>Replicated: nothing to gather until it respawns.</summary>
    [Export]
    public bool Depleted
    {
        get => _depleted;
        set
        {
            _depleted = value;
            if (IsInsideTree()) UpdateLook();
        }
    }

    /// <summary>Server only: when a depleted resource comes back (seconds, Time.GetTicksMsec / 1000).</summary>
    public double RespawnAt { get; private set; }

    public ResourceDef Def => ResourceCatalog.Get(Kind) ?? ResourceCatalog.Tree;

    public MultiplayerSynchronizer Sync => GetNode<MultiplayerSynchronizer>("Sync");

    private StandardMaterial3D _canopyMaterial = null!;

    private static double Now => Time.GetTicksMsec() / 1000.0;

    public override void _Ready()
    {
        InteractionId = InteractionPrefix + Name;
        _canopyMaterial = new StandardMaterial3D { Roughness = 0.9f };
        GetNode<MeshInstance3D>("Canopy").MaterialOverride = _canopyMaterial;
        UpdateLook();
        SetProcess(Main.IsServer);
    }

    /// <summary>Server: runs out now and comes back after the respawn time (the original's Respawn coroutine).</summary>
    public void Deplete()
    {
        Depleted = true;
        RespawnAt = Now + Def.RespawnSeconds;
    }

    public override void _Process(double delta)
    {
        if (Depleted && Now >= RespawnAt) Depleted = false;
    }

    private void UpdateLook()
    {
        if (_canopyMaterial != null) _canopyMaterial.AlbedoColor = Depleted ? DepletedColor : ActiveColor;
        Prompt = Depleted ? $"{Def.Name} (depleted)" : $"{Def.Verb} {Def.Name}";
    }
}
