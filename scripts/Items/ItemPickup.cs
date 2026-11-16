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
    /// <summary>How many (a dropped stack stays one pickup).</summary>
    [Export] public int Count { get; set; } = 1;
    /// <summary>Server only: the item's state (e.g. a dropped gun's loaded rounds), handed to whoever picks it up.</summary>
    public int? Data { get; set; }

    public MultiplayerSynchronizer Sync => GetNode<MultiplayerSynchronizer>("Sync");

    private Node3D _visual = null!;
    private float _time;

    public override void _Ready()
    {
        InteractionId = InteractionPrefix + Name;
        var isPass = ItemId == ItemCatalog.DungeonPassId;
        var label = Count > 1 ? $"{ItemCatalog.NameOf(ItemId)} x{Count}" : ItemCatalog.NameOf(ItemId);
        Prompt = $"Pick up {label}";

        _visual = GetNode<Node3D>("Visual");
        GetNode<Label3D>("Label").Text = label;
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
