using System.Collections.Generic;
using Godot;

namespace ExploreTogether;

/// <summary>
/// User command-line arguments, i.e. those after "--" on the Godot command line.
/// Supports "--flag" and "--key value".
/// </summary>
public static class CmdArgs
{
    private static Dictionary<string, string>? _args;

    private static Dictionary<string, string> Args
    {
        get
        {
            if (_args != null) return _args;
            _args = new Dictionary<string, string>();
            var raw = OS.GetCmdlineUserArgs();
            for (int i = 0; i < raw.Length; i++)
            {
                if (!raw[i].StartsWith("--")) continue;
                var key = raw[i][2..];
                var hasValue = i + 1 < raw.Length && !raw[i + 1].StartsWith("--");
                _args[key] = hasValue ? raw[++i] : "";
            }
            return _args;
        }
    }

    public static bool Has(string key) => Args.ContainsKey(key);

    public static string Get(string key, string fallback = "") =>
        Args.TryGetValue(key, out var v) && v != "" ? v : fallback;

    public static int GetInt(string key, int fallback) =>
        int.TryParse(Get(key), out var v) ? v : fallback;

    public static float GetFloat(string key, float fallback) =>
        float.TryParse(Get(key), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
