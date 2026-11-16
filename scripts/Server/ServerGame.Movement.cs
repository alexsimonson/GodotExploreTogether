using System.Collections.Generic;
using ExploreTogether.Core.Net;
using ExploreTogether.Player;
using Godot;

namespace ExploreTogether.Server;

/// <summary>
/// Server-side movement validation (city security review, findings 4 and 5). Clients still simulate their own
/// movement, but a submitted position is only accepted if:
/// - it's within the player's speed budget (<see cref="MoveGuard"/>): walking, sprinting, crouching, dragging or
///   holding someone, or held by a resident; and
/// - the straight line from the last accepted position doesn't pass through a wall, fence or closed door.
/// Otherwise the server sends the player back to the last accepted position. Everything that trusts a player's
/// position (reach checks, "from inside" door locks, dragging) now only sees accepted positions.
/// </summary>
public partial class ServerGame
{
    private readonly Dictionary<long, double> _lastCorrection = new();
    private readonly Dictionary<long, double> _lastMoveWarning = new();

    /// <summary>The player's top speed right now (m/s), from what the server knows about them.</summary>
    private float MaxSpeedOf(long peer, PlayerCharacter player)
    {
        if (!_sessions.TryGetValue(peer, out var s)) return PlayerSpeeds.Sprint;
        if (s.HeldByNpc != null) return PlayerSpeeds.Held;
        if (s.DraggingNpc != null || s.HoldingNpc != null) return PlayerSpeeds.Dragging;
        if (player.NetCrouching) return PlayerSpeeds.Crouch;
        return PlayerSpeeds.Sprint;
    }

    private bool AcceptMove(long peer, PlayerCharacter player, Vector3 position)
    {
        var now = Now;
        if (player.Guard is not { } guard) return true;
        var from = new Vector3(guard.X, guard.Y, guard.Z);
        var reason = "";
        if (!guard.Check(position.X, position.Y, position.Z, now, MaxSpeedOf(peer, player)))
            reason = "too fast";
        else if (position.DistanceSquaredTo(from) > 1e-6f && ThroughWall(player, from, position))
        {
            reason = "through a wall";
            guard.Reset(from.X, from.Y, from.Z, now); // undo the step the guard just accepted
        }
        if (reason == "") return true;

        if (!_lastMoveWarning.TryGetValue(peer, out var warned) || now - warned >= 2)
        {
            _lastMoveWarning[peer] = now;
            GD.Print($"[server] rejected a move by peer {peer} ({reason}): {from.Snapped(Vector3.One * 0.1f)} -> {position.Snapped(Vector3.One * 0.1f)}");
        }
        if (!_lastCorrection.TryGetValue(peer, out var corrected) || now - corrected >= 0.25)
        {
            _lastCorrection[peer] = now;
            _session.SendPositionCorrection(peer, from);
        }
        return false;
    }

    /// <summary>Does the line between two positions (at waist height) cross the world (walls, fences, closed doors)?</summary>
    private static bool ThroughWall(PlayerCharacter player, Vector3 from, Vector3 to)
    {
        if (!player.IsInsideTree()) return false;
        var lift = new Vector3(0, 0.9f, 0);
        var query = PhysicsRayQueryParameters3D.Create(from + lift, to + lift, collisionMask: 1, exclude: [player.GetRid()]);
        return player.GetWorld3D().DirectSpaceState.IntersectRay(query).Count > 0;
    }

    /// <summary>The server moves a player itself (e.g. pulled along while held): keep the guard and the client in step.</summary>
    private void PlacePlayer(PlayerCharacter player, Vector3 position)
    {
        player.Guard?.Reset(position.X, position.Y, position.Z, Now);
        player.ApplySubmittedTransform(position, player.NetYaw);
        _session.SendPositionCorrection(player.OwnerPeerId, position);
    }
}
