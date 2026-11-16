using ExploreTogether.Core.Dungeon;
using ExploreTogether.Dungeon;
using Godot;

namespace ExploreTogether.Net;

/// <summary>
/// Builds the node structure of an instance identically on server and client, so that
/// replication node paths match: Instances/Instance{id}/{Level, Players, PlayerSpawner, Items, ItemSpawner}.
///
/// On the server the container is a SubViewport with its own World3D, which gives each
/// instance a separate physics space and navigation map. The client only ever has its own
/// instance, rendered normally, so there the container is a plain Node3D.
/// </summary>
public static class InstanceFactory
{
    public const string PlayerScenePath = "res://scenes/Player.tscn";
    public const string HubScenePath = "res://scenes/levels/Hub.tscn";
    public const string ItemScenePath = "res://scenes/props/ItemPickup.tscn";

    public static string NodeName(int instanceId) => $"Instance{instanceId}";

    /// <summary>The id of the instance a node belongs to (from its Instance{id} ancestor), or -1.</summary>
    public static int InstanceIdOf(Node node)
    {
        for (var n = node; n != null; n = n.GetParent())
            if (n.Name.ToString() is var name && name.StartsWith("Instance") && int.TryParse(name["Instance".Length..], out var id))
                return id;
        return -1;
    }

    /// <param name="layoutData">For dungeons, the exported maze layout. Ignored for the hub.</param>
    public static InstanceNodes Build(Node instancesRoot, int instanceId, string kind, int[] layoutData, bool isServer)
    {
        Node container = isServer
            ? new SubViewport
            {
                OwnWorld3D = true,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
                Size = new Vector2I(2, 2),
            }
            : new Node3D();
        container.Name = NodeName(instanceId);
        instancesRoot.AddChild(container);

        Node level = kind switch
        {
            InstanceKinds.Hub => GD.Load<PackedScene>(HubScenePath).Instantiate(),
            InstanceKinds.Dungeon => DungeonBuilder.Build(MazeLayout.Import(layoutData)),
            _ => throw new System.ArgumentException($"Unknown instance kind '{kind}'"),
        };
        level.Name = "Level";
        container.AddChild(level);

        var players = new Node3D { Name = "Players" };
        container.AddChild(players);

        var spawner = new MultiplayerSpawner { Name = "PlayerSpawner", SpawnPath = new NodePath("../Players") };
        spawner.AddSpawnableScene(PlayerScenePath);
        container.AddChild(spawner);

        var items = new Node3D { Name = "Items" };
        container.AddChild(items);
        var itemSpawner = new MultiplayerSpawner { Name = "ItemSpawner", SpawnPath = new NodePath("../Items") };
        itemSpawner.AddSpawnableScene(ItemScenePath);
        container.AddChild(itemSpawner);

        return new InstanceNodes(container, level, players, items);
    }

    /// <summary>Finds an interactable by id anywhere in an instance (level props and items).</summary>
    public static Interactable? FindInteractable(Node container, string interactionId)
    {
        foreach (var child in container.FindChildren("*", nameof(StaticBody3D), recursive: true, owned: false))
            if (child is Interactable i && i.InteractionId == interactionId)
                return i;
        return null;
    }
}

public static class InstanceKinds
{
    public const string Hub = "hub";
    public const string Dungeon = "dungeon";
}

public readonly record struct InstanceNodes(Node Container, Node Level, Node3D Players, Node3D Items);
