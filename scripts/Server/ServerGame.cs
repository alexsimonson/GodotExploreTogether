using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ExploreTogether.Core;
using ExploreTogether.Core.Persistence;
using ExploreTogether.Net;
using Godot;

namespace ExploreTogether.Server;

/// <summary>Server-side state for one connected peer.</summary>
public sealed class PeerSession(long peer)
{
    public long Peer { get; } = peer;
    public AccountRecord? Account { get; set; }
    public CharacterRecord? Character { get; set; }
    public int? PendingInstanceId { get; set; }
    /// <summary>True while a password hash is being computed for this peer.</summary>
    public bool AuthInProgress { get; set; }
    /// <summary>When play time was last added to the database for this character.</summary>
    public DateTime PlayTimeCountedUntil { get; set; }
}

/// <summary>
/// Server-only root. Listens for clients and runs the session flow:
///   connect -> Login/Register -> character list -> Create/SelectCharacter
///   -> EnterInstance -> client builds it -> InstanceReady -> player spawned.
/// Owns the database; character progress is saved on disconnect, every AutosaveSeconds, and at shutdown.
/// </summary>
public partial class ServerGame : Node
{
    private const int MaxClients = 64;
    private const double AutosaveSeconds = 15;

    private NetSession _session = null!;
    private InstanceManager _instances = null!;
    private GameDatabase _db = null!;
    private readonly Dictionary<long, PeerSession> _sessions = new();

    public override void _Ready()
    {
        var main = GetParent<Main>();
        _session = main.Session;

        var dbPath = ServerPaths.DatabasePath();
        try
        {
            _db = GameDatabase.Open(dbPath);
        }
        catch (Exception e)
        {
            // Includes DatabaseTooNewException and a failing migration (the file stays at its last good version).
            GD.PushError($"[server] could not open database {dbPath}: {e.Message}");
            _db = null!;
            GetTree().Quit(1);
            return;
        }
        if (_db.BackupPath != null)
            GD.Print($"[server] backed up database before upgrading: {_db.BackupPath}");
        GD.Print($"[server] database {dbPath} (schema v{_db.SchemaVersion})");

        _instances = new InstanceManager(main.Instances);
        _instances.CreateHub();

        var port = CmdArgs.GetInt("port", GameInfo.DefaultPort);
        var peer = new ENetMultiplayerPeer();
        var err = peer.CreateServer(port, MaxClients);
        if (err != Error.Ok)
        {
            GD.PushError($"[server] failed to listen on port {port}: {err}");
            GetTree().Quit(1);
            return;
        }
        Multiplayer.MultiplayerPeer = peer;
        Multiplayer.PeerConnected += id =>
        {
            _sessions[id] = new PeerSession(id);
            GD.Print($"[server] peer {id} connected");
        };
        Multiplayer.PeerDisconnected += OnPeerDisconnected;

        _session.LoginRequested += OnLogin;
        _session.RegisterRequested += OnRegister;
        _session.CreateCharacterRequested += OnCreateCharacter;
        _session.SelectCharacterRequested += OnSelectCharacter;
        _session.InstanceReadyReceived += OnInstanceReady;

        var autosave = new Timer { WaitTime = AutosaveSeconds, Autostart = true };
        autosave.Timeout += SaveAll;
        AddChild(autosave);

        GD.Print($"[server] listening on port {port}");
    }

    public override void _ExitTree()
    {
        if (_db == null) return;
        SaveAll();
        _db.Dispose();
        GD.Print("[server] saved and closed database");
    }

    // ------------------------------------------------------------------ auth

    private async void OnLogin(long peer, string username, string password)
    {
        if (!_sessions.TryGetValue(peer, out var s) || s.AuthInProgress) return;
        if (s.Account != null)
        {
            _session.SendAuthResult(peer, false, "already_logged_in", "Already logged in.");
            return;
        }

        var account = _db.FindAccount(username.StripEdges());
        s.AuthInProgress = true;
        // Hashing is slow on purpose; do it off the game thread. 'await' resumes on the game thread.
        var ok = account != null && await Task.Run(() => PasswordHasher.Verify(password, account.PasswordHash));
        s.AuthInProgress = false;
        if (!_sessions.ContainsKey(peer)) return; // disconnected meanwhile

        if (!ok)
        {
            _session.SendAuthResult(peer, false, "invalid", "Invalid username or password.");
            return;
        }
        CompleteLogin(s, account!);
    }

    private async void OnRegister(long peer, string username, string password)
    {
        if (!_sessions.TryGetValue(peer, out var s) || s.AuthInProgress) return;
        if (s.Account != null)
        {
            _session.SendAuthResult(peer, false, "already_logged_in", "Already logged in.");
            return;
        }

        username = username.StripEdges();
        var error = GameRules.ValidateUsername(username) ?? GameRules.ValidatePassword(password);
        if (error != null)
        {
            _session.SendAuthResult(peer, false, "invalid", error);
            return;
        }
        if (_db.FindAccount(username) != null)
        {
            _session.SendAuthResult(peer, false, "taken", "That username is taken.");
            return;
        }

        s.AuthInProgress = true;
        var hash = await Task.Run(() => PasswordHasher.Hash(password));
        s.AuthInProgress = false;
        if (!_sessions.ContainsKey(peer)) return;

        var account = _db.CreateAccount(username, hash);
        if (account == null) // registered by someone else while we were hashing
        {
            _session.SendAuthResult(peer, false, "taken", "That username is taken.");
            return;
        }
        GD.Print($"[server] registered account '{username}'");
        CompleteLogin(s, account);
    }

    private void CompleteLogin(PeerSession s, AccountRecord account)
    {
        if (_sessions.Values.Any(o => o.Account?.Id == account.Id))
        {
            _session.SendAuthResult(s.Peer, false, "already_online", "That account is already logged in.");
            return;
        }
        s.Account = account;
        _db.RecordLogin(account.Id);
        GD.Print($"[server] peer {s.Peer} logged in as '{account.Username}'");
        _session.SendAuthResult(s.Peer, true, "ok", "");
        SendCharacterList(s);
    }

    // ------------------------------------------------------------ characters

    private void SendCharacterList(PeerSession s)
    {
        var list = _db.ListCharacters(s.Account!.Id)
            .Select(c => new CharacterSummary(c.Id, c.Name, ColorFromHex(c.ColorHex), c.PlayTimeSeconds));
        _session.SendCharacterList(s.Peer, list);
    }

    private void OnCreateCharacter(long peer, string name, Color color)
    {
        if (!_sessions.TryGetValue(peer, out var s) || s.Account == null || s.Character != null) return;

        name = name.StripEdges();
        var error = GameRules.ValidateCharacterName(name);
        if (error != null)
        {
            _session.SendCharacterResult(peer, false, error);
            return;
        }

        var hex = PlayerLook.Sanitize(color).ToHtml(false);
        var (result, character) = _db.CreateCharacter(s.Account.Id, name, hex);
        switch (result)
        {
            case CreateResult.NameTaken:
                _session.SendCharacterResult(peer, false, "That character name is taken.");
                return;
            case CreateResult.LimitReached:
                _session.SendCharacterResult(peer, false, $"You can have at most {GameRules.MaxCharactersPerAccount} characters.");
                return;
        }
        GD.Print($"[server] '{s.Account.Username}' created character '{character!.Name}'");
        _session.SendCharacterResult(peer, true, "");
        SendCharacterList(s);
    }

    private void OnSelectCharacter(long peer, long characterId)
    {
        if (!_sessions.TryGetValue(peer, out var s) || s.Account == null || s.Character != null) return;

        var character = _db.GetCharacter(characterId);
        if (character == null || character.AccountId != s.Account.Id)
        {
            _session.SendCharacterResult(peer, false, "Character not found.");
            return;
        }

        s.Character = character;
        s.PlayTimeCountedUntil = DateTime.UtcNow;
        var target = _instances.Hub;
        s.PendingInstanceId = target.Id;
        _session.SendEnterInstance(peer, target.Id, target.Kind);
    }

    private void OnInstanceReady(long peer, int instanceId)
    {
        if (!_sessions.TryGetValue(peer, out var s) || s.Character == null || s.PendingInstanceId != instanceId)
        {
            GD.PushWarning($"[server] unexpected InstanceReady({instanceId}) from peer {peer}");
            return;
        }
        s.PendingInstanceId = null;
        var instance = _instances.Get(instanceId);
        if (instance == null) return;

        var c = s.Character;
        var saved = instance == _instances.Hub ? c.HubPosition : null;
        _instances.AddPlayer(instance, peer, c.Name, ColorFromHex(c.ColorHex),
            saved is { } p ? new Vector3(p.X, p.Y, p.Z) : null, saved?.Yaw ?? 0f);
    }

    // ---------------------------------------------------------------- saving

    private void OnPeerDisconnected(long peer)
    {
        GD.Print($"[server] peer {peer} disconnected");
        if (_sessions.Remove(peer, out var s)) Save(s);
        _instances.RemovePeer(peer);
    }

    private void SaveAll()
    {
        foreach (var s in _sessions.Values) Save(s);
    }

    private void Save(PeerSession s)
    {
        if (s.Character == null) return;

        var now = DateTime.UtcNow;
        var playDelta = (now - s.PlayTimeCountedUntil).TotalSeconds;
        s.PlayTimeCountedUntil = now;

        SavedPosition? hubPos = null;
        if (_instances.FindPlayer(s.Peer) is { } found && found.Instance == _instances.Hub)
        {
            var pos = found.Player.NetPosition;
            hubPos = new SavedPosition(pos.X, pos.Y, pos.Z, found.Player.NetYaw);
        }

        _db.SaveCharacterProgress(s.Character.Id, playDelta, hubPos);
        s.Character = s.Character with
        {
            PlayTimeSeconds = s.Character.PlayTimeSeconds + playDelta,
            HubPosition = hubPos ?? s.Character.HubPosition,
        };
    }

    private static Color ColorFromHex(string hex) => Color.FromString("#" + hex, PlayerLook.DefaultColor);
}
