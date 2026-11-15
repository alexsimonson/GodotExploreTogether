using System.IO;
using Godot;

namespace ExploreTogether.Server;

/// <summary>
/// Where the server keeps its files. Never Godot's user:// (which defaults to the C: drive).
///   --db PATH   explicit database path
///   default     running from the project folder: &lt;project&gt;/.localdata/server.db
///               exported build: &lt;executable folder&gt;/data/server.db
/// </summary>
public static class ServerPaths
{
    public static string DatabasePath()
    {
        var explicitPath = CmdArgs.Get("db");
        if (explicitPath != "") return Path.GetFullPath(explicitPath);

        if (OS.HasFeature("template")) // exported build
            return Path.Combine(Path.GetDirectoryName(OS.GetExecutablePath())!, "data", "server.db");

        var dataDir = Path.Combine(ProjectSettings.GlobalizePath("res://"), ".localdata");
        Directory.CreateDirectory(dataDir);
        // Keep Godot from scanning the data folder as project assets.
        var gdignore = Path.Combine(dataDir, ".gdignore");
        if (!File.Exists(gdignore)) File.WriteAllText(gdignore, "");
        return Path.Combine(dataDir, "server.db");
    }
}
