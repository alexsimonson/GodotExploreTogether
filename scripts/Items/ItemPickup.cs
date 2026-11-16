using ExploreTogether.Core.Items;
using ExploreTogether.Dungeon;
using Godot;

namespace ExploreTogether.Items;

/// <summary>
/// An item lying in an instance (port of the original Item/ItemSpawn pickup). Spawned by the
/// server through the instance's ItemSpawner and replicated (item id and position at spawn) to the
/// peers in that instance. Picking it up is an interaction ("item:&lt;node name&gt;") the server validates.
/// </summary>
public partial class ItemPickup : Interactable
{
    public const string InteractionPrefix = "item:";

    [Export] public int ItemId { get; set; }

    public MultiplayerSynchronizer Sync => GetNode<MultiplayerSynchronizer>("Sync");

    private Node3D _visual = null!;
    private float _time;

    public override void _Ready()
    {
        InteractionId = InteractionPrefix + Name;
        var isPass = ItemId == ItemCatalog.DungeonPassId;
        Prompt = $"Pick up {ItemCatalog.NameOf(ItemId)}";

        _visual = GetNode<Node3D>("Visual");
        GetNode<Label3D>("Label").Text = ItemCatalog.NameOf(ItemId);
        GetNode<MeshInstance3D>("Visual/Loot").Visible = !isPass;
        GetNode<Node3D>("Visual/Pass").Visible = isPass;
        GetNode<OmniLight3D>("Glow").Visible = isPass;

        _time = (float)GD.RandRange(0, Mathf.Tau);
        SetProcess(!Main.IsServer);
    }

    public override void _Process(double delta)
    {
        // Gentle bob and spin so items catch the eye (client only).
        _time += (float)delta;
        _visual.Position = new Vector3(0, 0.1f * Mathf.Sin(_time * 2f), 0);
        _visual.RotateY((float)delta * 1.2f);
    }
}
