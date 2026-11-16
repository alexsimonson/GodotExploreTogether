using System.Collections.Generic;
using ExploreTogether.Core.Net;
using ExploreTogether.Net;
using Godot;

namespace ExploreTogether.Server;

/// <summary>
/// Protection against clients flooding the server (item security review, findings 2 and 5):
/// per-peer request rate limits, and Godot's client-to-client relay turned off.
/// </summary>
public partial class ServerGame
{
    /// <summary>
    /// Limits per request (NetSession RPC name). Generous for normal play, including the dev tools
    /// (--dev-solve sends one Interact per item in the dungeon at once). Requests over a limit are ignored.
    /// </summary>
    private static readonly Dictionary<string, RateLimit> RequestLimits = new()
    {
        ["PlayerTransform"] = new(90, 120), // sent each physics frame (60/s) while moving
        ["Aim"] = new(30, 30),              // ~15/s while aiming a wand
        ["Attack"] = new(10, 10),           // weapons have their own, shorter cooldowns
        ["Reload"] = new(5, 5),
        ["Interact"] = new(10, 40),
        ["LockDoor"] = new(5, 10),
        ["DragBody"] = new(5, 10),
        ["MoveItem"] = new(10, 20),
        ["DropItem"] = new(10, 20),
        ["InstanceReady"] = new(2, 5),
        ["CreateDungeon"] = new(2, 5),
        ["JoinDungeon"] = new(2, 5),
        ["Login"] = new(1, 3),
        ["Register"] = new(1, 3),
        ["CreateCharacter"] = new(2, 5),
        ["SelectCharacter"] = new(2, 5),
    };

    private readonly RequestRateLimiter _rateLimiter = new(RequestLimits);
    /// <summary>When each peer was last warned about in the log, so a flood doesn't also flood the log.</summary>
    private readonly Dictionary<long, double> _lastRateWarning = new();

    private void SetUpRequestLimits()
    {
        // Clients never need to send RPCs to each other through the server (finding 5).
        if (Multiplayer is SceneMultiplayer sm) sm.ServerRelay = false;

        _session.RequestFilter = AllowRequest;
        Multiplayer.PeerDisconnected += id =>
        {
            _rateLimiter.Remove(id);
            _lastRateWarning.Remove(id);
        };
    }

    private bool AllowRequest(long peer, string kind)
    {
        var now = Time.GetTicksMsec() / 1000.0;
        var decision = _rateLimiter.Check(peer, kind, now);
        if (decision == RateDecision.Allow) return true;

        if (decision == RateDecision.Disconnect)
        {
            GD.PushWarning($"[server] disconnecting peer {peer}: too many requests over the rate limit (last: {kind})");
            Multiplayer.MultiplayerPeer.DisconnectPeer((int)peer);
            return false;
        }
        if (!_lastRateWarning.TryGetValue(peer, out var last) || now - last >= 5)
        {
            _lastRateWarning[peer] = now;
            GD.Print($"[server] rate limit: ignoring {kind} requests from peer {peer}");
        }
        return false;
    }
}
