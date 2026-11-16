using System;
using System.Linq;
using ExploreTogether.Core.Net;
using Godot;

namespace ExploreTogether.Server;

/// <summary>
/// Running a server reachable from the internet, for light testing with friends (rules: Core.Net.PublicServer;
/// setup: docs/DEV-SERVER.md).
///
/// <c>--public</c> turns it on:
/// - Any <c>--dev-*</c> switch is refused: the server won't start.
/// - Registering needs the invite code, read from the <c>EXPLORE_INVITE_CODE</c> environment variable (or the file
///   named by <c>--invite-code-file</c>); at least 8 characters. Without one, the server won't start, unless
///   <c>--open-registration</c> says registration is meant to be open.
/// - Caps (each can be changed with its argument): <c>--max-accounts</c> 100, <c>--max-players</c> 16,
///   <c>--max-connections-per-address</c> 4, <c>--max-cities</c> 3, <c>--max-dungeons</c> 6.
/// - An address with 5 failed logins (or wrong invite codes) in 5 minutes is banned for 10 minutes.
///
/// Without <c>--public</c> (local development) there are no caps, and an invite code is only checked if one is set;
/// but the server warns loudly when the debug supply is on.
/// </summary>
public partial class ServerGame
{
    private static readonly bool Public = CmdArgs.Has("public");
    private static readonly int MaxPlayers = CmdArgs.GetInt("max-players", Public ? PublicServer.DefaultMaxPlayers : 64);
    private static readonly int MaxAccounts = CmdArgs.GetInt("max-accounts", Public ? PublicServer.DefaultMaxAccounts : int.MaxValue);
    private static readonly int MaxCities = CmdArgs.GetInt("max-cities", Public ? PublicServer.DefaultMaxCities : int.MaxValue);
    private static readonly int MaxDungeons = CmdArgs.GetInt("max-dungeons", Public ? PublicServer.DefaultMaxDungeons : int.MaxValue);
    private readonly AddressGuard _addressGuard = new(maxConnections:
        CmdArgs.GetInt("max-connections-per-address", Public ? PublicServer.DefaultMaxConnectionsPerAddress : int.MaxValue));
    private string _inviteCode = "";

    /// <summary>Checks the startup rules for a public server; false (and quits) if it mustn't start.</summary>
    private bool CheckPublicPolicy()
    {
        _inviteCode = ReadInviteCode();
        if (!Public)
        {
            if (CmdArgs.Has("dev-supply"))
                GD.PushWarning("[server] WARNING: the debug supply is ON (--dev-supply): anyone at the hub chest can take any item. " +
                               "Never use this on a server others can reach.");
            return true;
        }

        var forbidden = PublicServer.ForbiddenArguments(CmdArgs.Names);
        if (forbidden.Count > 0)
            return Refuse($"a public server can't run with dev switches: --{string.Join(", --", forbidden)}");
        if (_inviteCode == "" && !CmdArgs.Has("open-registration"))
            return Refuse("a public server needs an invite code (set EXPLORE_INVITE_CODE or --invite-code-file), " +
                          "or --open-registration to let anyone register");
        if (_inviteCode != "" && !PublicServer.InviteCodeStrongEnough(_inviteCode))
            return Refuse("the invite code is too short: use at least 8 characters");
        GD.Print($"[server] PUBLIC mode: registration {(_inviteCode != "" ? "by invite code" : "OPEN")}, " +
                 $"max {MaxPlayers} players, {MaxAccounts} accounts, {MaxCities} cities, {MaxDungeons} dungeons, " +
                 $"{_addressGuard.MaxConnections} connections per address");
        return true;
    }

    private bool Refuse(string why)
    {
        GD.PushError($"[server] refusing to start: {why}");
        GetTree().Quit(2);
        return false;
    }

    private static string ReadInviteCode()
    {
        if (CmdArgs.Get("invite-code-file") is { Length: > 0 } file)
        {
            try { return System.IO.File.ReadAllText(file).Trim(); }
            catch (Exception e) { GD.PushError($"[server] can't read the invite code file {file}: {e.Message}"); return ""; }
        }
        return (System.Environment.GetEnvironmentVariable("EXPLORE_INVITE_CODE") ?? "").Trim();
    }

    private string AddressOf(long peer) =>
        Multiplayer.MultiplayerPeer is ENetMultiplayerPeer enet && enet.GetPeer((int)peer) is { } p ? p.GetRemoteAddress() : "?";

    /// <summary>A new connection: refused (disconnected) if its address is banned or has too many connections.</summary>
    private bool AdmitConnection(PeerSession s)
    {
        if (_addressGuard.TryConnect(s.Address, Now)) return true;
        GD.Print($"[server] refused peer {s.Peer} from {s.Address}: " +
                 (_addressGuard.IsBanned(s.Address, Now) ? "banned for failed logins" : "too many connections from that address"));
        _sessions.Remove(s.Peer);
        Multiplayer.MultiplayerPeer.DisconnectPeer((int)s.Peer);
        return false;
    }

    private void LoginFailed(PeerSession s, string what)
    {
        if (!_addressGuard.Failed(s.Address, Now)) return;
        GD.Print($"[server] banning {s.Address} for {_addressGuard.BanSeconds / 60:0} minutes after repeated failures ({what})");
        _session.SendAuthResult(s.Peer, false, "banned", "Too many failed attempts. Try again later.");
        Multiplayer.MultiplayerPeer.DisconnectPeer((int)s.Peer);
    }

    private void LoginSucceeded(PeerSession s) => _addressGuard.Succeeded(s.Address);

    /// <summary>Registering: the invite code (if the server has one) and the account cap.</summary>
    private bool RegistrationAllowed(PeerSession s, string invite)
    {
        if (_inviteCode != "" && !PublicServer.InviteCodeMatches(_inviteCode, invite))
        {
            _session.SendAuthResult(s.Peer, false, "invite", "That invite code isn't right. Ask whoever runs the server.");
            GD.Print($"[server] peer {s.Peer} from {s.Address}: wrong invite code");
            LoginFailed(s, "wrong invite code");
            return false;
        }
        if (_db.CountAccounts() >= MaxAccounts)
        {
            _session.SendAuthResult(s.Peer, false, "full", "This server isn't taking new accounts.");
            return false;
        }
        return true;
    }
}
