using System.Linq;
using ExploreTogether.Core;
using Godot;

namespace ExploreTogether;

/// <summary>Entry point. Decides whether this process is the dedicated server or a client.</summary>
public partial class Main : Node
{
    public static bool IsServer { get; private set; }

    public override void _Ready()
    {
        IsServer = OS.GetCmdlineUserArgs().Contains("--server")
                   || OS.GetCmdlineArgs().Contains("--server")
                   || OS.HasFeature("dedicated_server");

        GD.Print($"{GameInfo.Name} starting as {(IsServer ? "SERVER" : "CLIENT")} " +
                 $"(Godot {Engine.GetVersionInfo()["string"]}, .NET {System.Environment.Version})");
    }
}
