using ExploreTogether.Client;
using ExploreTogether.Core;
using ExploreTogether.Net;
using ExploreTogether.Server;
using Godot;

namespace ExploreTogether;

/// <summary>
/// Entry point. Decides whether this process is the dedicated server or a client,
/// and creates the matching game node.
///
/// User args (after "--"):
///   --server                    run as dedicated server
///   --port N                    server listen port / default client port
///   --db PATH                   server: SQLite database file (default: &lt;project&gt;/.localdata/server.db)
///   --connect host[:port]       client: server address for the login screen / auto-login
///   --user NAME                 client: dev auto-login as NAME (registers the account if missing)
///   --password P                client: password for auto-login (default "devpass")
///   --character NAME            client: character to auto-play, created if missing (default: the user name)
///   --color COLOR               client: color for an auto-created character ("#3cb043" or "green")
///   --dev-quit-after SECONDS    quit automatically (for scripted tests)
///   --dev-autowalk              client: walk in a circle without input
///   --dev-log                   client: periodically log remote player positions
/// </summary>
public partial class Main : Node
{
    public static bool IsServer { get; private set; }

    public NetSession Session { get; private set; } = null!;
    public Node Instances { get; private set; } = null!;

    public override void _Ready()
    {
        IsServer = CmdArgs.Has("server") || OS.HasFeature("dedicated_server");
        Session = GetNode<NetSession>("Session");
        Instances = GetNode<Node>("Instances");

        GD.Print($"{GameInfo.Name} starting as {(IsServer ? "SERVER" : "CLIENT")} " +
                 $"(Godot {Engine.GetVersionInfo()["string"]}, .NET {System.Environment.Version})");

        if (IsServer)
        {
            AddChild(new ServerGame { Name = "ServerGame" });
        }
        else
        {
            InputSetup.EnsureActions();
            AddChild(new ClientGame { Name = "ClientGame" });
        }

        var quitAfter = CmdArgs.GetFloat("dev-quit-after", 0);
        if (quitAfter > 0)
            GetTree().CreateTimer(quitAfter).Timeout += () => GetTree().Quit();
    }
}
