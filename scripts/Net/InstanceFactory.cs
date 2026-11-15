using Godot;

namespace ExploreTogether.Net;

/// <summary>
/// Builds the node structure of an instance identically on server and client, so that
/// replication node paths match: Instances/Instance{id}/{Level, Players, PlayerSpawner}.
///
/// On the server the container is a SubViewport with its own World3D, which gives each
/// instance a separate physics space and navigation map. The client only ever has its own
/// instance, rendered normally, so there the container is a plain Node3D.
/// </summary>
public static class InstanceFactory
{
    public const string PlayerScenePath = "res://scenes/Player.tscn";

    public static string NodeName(int instanceId) => $"Instance{instanceId}";

    public static string LevelScenePath(string kind) => kind switch
    {
        InstanceKinds.Hub => "res://scenes/levels/Hub.tscn",
        _ => throw new System.ArgumentException($"Unknown instance kind '{kind}'"),
    };

    public static InstanceNodes Build(Node instancesRoot, int instanceId, string kind, bool isServer)
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

        var level = GD.Load<PackedScene>(LevelScenePath(kind)).Instantiate();
        level.Name = "Level";
        container.AddChild(level);

        var players = new Node3D { Name = "Players" };
        container.AddChild(players);

        var spawner = new MultiplayerSpawner { Name = "PlayerSpawner", SpawnPath = new NodePath("../Players") };
        spawner.AddSpawnableScene(PlayerScenePath);
        container.AddChild(spawner);

        return new InstanceNodes(container, players);
    }
}

public static class InstanceKinds
{
    public const string Hub = "hub";
}

public readonly record struct InstanceNodes(Node Container, Node3D Players);
