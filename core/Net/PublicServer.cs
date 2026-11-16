using System.Security.Cryptography;
using System.Text;

namespace ExploreTogether.Core.Net;

/// <summary>
/// Rules for a server reachable from the internet (<c>--public</c>), for light testing with friends. See
/// docs/DEV-SERVER.md. Plaintext game traffic remains a known gap (passwords cross the network unencrypted).
///
/// - No dev switches: every <c>--dev-*</c> argument is a cheat or a test hook (free items, skipped reach checks,
///   residents that ignore you...). A public server refuses to start with any of them.
/// - Registration needs an invite code (set on the server; never in the repository). Without one a public server
///   refuses to start, unless registration is explicitly left open.
/// - Caps: accounts, players, connections per address, simultaneous cities and dungeons.
/// - Failed logins: an address with too many failures in a short time is banned for a while (and disconnected).
/// </summary>
public static class PublicServer
{
    public const int DefaultMaxAccounts = 100;
    public const int DefaultMaxPlayers = 16;
    public const int DefaultMaxConnectionsPerAddress = 4;
    public const int DefaultMaxCities = 3;
    public const int DefaultMaxDungeons = 6;

    /// <summary>The arguments a public server refuses (all dev switches).</summary>
    public static IReadOnlyList<string> ForbiddenArguments(IEnumerable<string> argumentNames) =>
        argumentNames.Where(a => a.StartsWith("dev-", StringComparison.Ordinal)).OrderBy(a => a).ToList();

    /// <summary>Does the code given match the server's? Constant-time, case-sensitive, ignoring surrounding spaces.</summary>
    public static bool InviteCodeMatches(string expected, string? given)
    {
        var a = Encoding.UTF8.GetBytes(expected.Trim());
        var b = Encoding.UTF8.GetBytes((given ?? "").Trim());
        return a.Length > 0 && CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>Is the invite code strong enough to keep strangers out (at least 8 characters)?</summary>
    public static bool InviteCodeStrongEnough(string code) => code.Trim().Length >= 8;
}

/// <summary>
/// Per-address limits on a public server: too many failed logins within <see cref="Window"/> bans the address for
/// <see cref="BanSeconds"/>; and an address may hold only so many connections at once. Times are seconds (any clock).
/// </summary>
public sealed class AddressGuard(int maxFailures = 5, double window = 300, double banSeconds = 600, int maxConnections = PublicServer.DefaultMaxConnectionsPerAddress)
{
    public int MaxFailures { get; } = maxFailures;
    public double Window { get; } = window;
    public double BanSeconds { get; } = banSeconds;
    public int MaxConnections { get; } = maxConnections;

    private readonly Dictionary<string, List<double>> _failures = new();
    private readonly Dictionary<string, double> _bannedUntil = new();
    private readonly Dictionary<string, int> _connections = new();

    public bool IsBanned(string address, double now) => _bannedUntil.TryGetValue(address, out var until) && now < until;

    /// <summary>A new connection from this address: false if it's banned or already at its limit (refuse it).</summary>
    public bool TryConnect(string address, double now)
    {
        if (IsBanned(address, now)) return false;
        var count = _connections.GetValueOrDefault(address);
        if (count >= MaxConnections) return false;
        _connections[address] = count + 1;
        return true;
    }

    public void Disconnected(string address)
    {
        if (_connections.TryGetValue(address, out var count))
        {
            if (count <= 1) _connections.Remove(address);
            else _connections[address] = count - 1;
        }
    }

    /// <summary>A failed login (or a wrong invite code). Returns true if that got the address banned.</summary>
    public bool Failed(string address, double now)
    {
        var list = _failures.TryGetValue(address, out var l) ? l : _failures[address] = [];
        list.RemoveAll(t => now - t > Window);
        list.Add(now);
        if (list.Count < MaxFailures) return false;
        _bannedUntil[address] = now + BanSeconds;
        list.Clear();
        return true;
    }

    public void Succeeded(string address) => _failures.Remove(address);
}
