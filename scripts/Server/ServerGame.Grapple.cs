using System.Linq;
using ExploreTogether.City;
using ExploreTogether.Core.City;
using ExploreTogether.Core.Items;
using ExploreTogether.Core.Persistence;
using ExploreTogether.Net;
using Godot;
using Noise = ExploreTogether.Core.City.Noise;

namespace ExploreTogether.Server;

/// <summary>
/// City actions (one request, <c>CityAction(action, target)</c>): crouch, grab, struggle, kick, pick, pickcancel.
///
/// Grappling, both ways (numbers in Core.City.Breaching):
/// - F on a standing resident grabs them. They're pulled along in front of you; each second they may wriggle free
///   (tougher people more often; half as often when grabbed by surprise from behind, when they also can't cry out).
///   Hold the attack button to strangle them: a meter fills over <see cref="Breaching.StrangleSeconds"/> and then
///   they're subdued (unconscious). Strangled, they can't cry out; they fight hard at first and weaken as it goes;
///   ease off and they recover. F again lets go. Either way, once free (or awake) they remember you.
/// - Police, security and brave residents grab players to arrest or detain them. Held, you can barely move; mash F to
///   struggle free, which counts as resisting (police then use force). Police holding you for
///   <see cref="Breaching.ArrestSeconds"/> arrest you: you're escorted out of the city, back to the hub.
///
/// Breaking in (plan: DOOR-BREACHING-PLAN.md): K kicks a closed door from outside; holding L on a locked door
/// without its key picks it with a lockpick (a timed action the server checks: moving away or getting hurt cancels).
/// Shotgun slugs through a deadbolt: ServerGame.Combat's hit scan calls <see cref="BreakDoorLock"/>.
/// </summary>
public partial class ServerGame
{
    private void SetUpCityActions() => _session.CityActionRequested += OnCityAction;

    private void OnCityAction(long peer, string action, string target)
    {
        if (!_sessions.TryGetValue(peer, out var s) || s.Character == null || s.Health.IsDead) return;
        if (_instances.FindPlayer(peer) is not { } found) return;
        switch (action)
        {
            case "crouch":
                found.Player.NetCrouching = target == "1";
                break;
            case "grab":
                if (found.Instance.Kind != InstanceKinds.City) return;
                if (s.HeldByNpc != null) Struggle(s, found.Instance);
                else if (s.HoldingNpc != null || target == "") LetGoOfResident(s, escaped: false, notify: true);
                else GrabResident(s, found.Instance, target);
                break;
            case "strangle":
                s.Strangling = target == "1" && s.HoldingNpc != null;
                if (!s.Strangling && s.StrangleProgress <= 0) _session.SendProgress(peer, "", 0);
                break;
            case "struggle":
                if (s.HeldByNpc != null) Struggle(s, found.Instance);
                break;
            case "kick":
                KickDoor(s, target);
                break;
            case "pick":
                StartPick(s, target);
                break;
            case "pickcancel":
                CancelPick(s, notify: true);
                break;
        }
    }

    // ------------------------------------------------------------------ players holding residents

    private void GrabResident(PeerSession s, ServerInstance city, string interactionId)
    {
        if (!interactionId.StartsWith(CityNpc.LootPrefix) || !IsNear(s, interactionId, out _)) return;
        if (!city.Npcs.TryGetValue(interactionId[CityNpc.LootPrefix.Length..], out var npc) || npc.IsDown || npc.HeldBy != 0) return;
        var me = _instances.FindPlayer(s.Peer)!.Value.Player.NetPosition;
        var d = new Vector2(me.X - npc.Position.X, me.Z - npc.Position.Z).Length();
        if (d > Breaching.GrabReach + 0.4f || Mathf.Abs(me.Y - npc.Position.Y) > 1.2f)
        {
            _session.SendNotice(s.Peer, "Too far to grab.");
            return;
        }
        LetGoOfBody(s, notify: true);
        if (npc.Holding != 0) NpcRelease(npc, "is pulled off");
        var surprised = Breaching.FromBehind(npc.Brain.Yaw, npc.Position.X, npc.Position.Z, me.X, me.Z) && npc.Brain.Mood <= Mood.Curious;
        npc.HeldBy = s.Peer;
        s.HoldingNpc = npc.Name;
        s.HoldingSince = Now;
        s.HoldingSurprised = surprised;
        s.NextEscapeRoll = Now + 1;
        (s.Strangling, s.StrangleProgress) = (false, 0);
        npc.Brain.GrabbedBy(s.Peer, me.X, me.Z, surprised);
        _session.SendGrabState(s.Peer, "holding", npc.NetName);
        _session.SendNotice(s.Peer, surprised ? $"You grab {npc.NetName} from behind. Hold left mouse to strangle, F to let go."
                                              : $"You grab {npc.NetName}! They're struggling. Hold left mouse to strangle, F to let go.");
        GD.Print($"[server] '{s.Character!.Name}' grabs {npc.NetName} ({npc.Role}, grit {npc.Brain.Person.Grit:0.00})" +
                 (surprised ? " from behind" : ""));
    }

    /// <summary>Lets go of the resident this player holds (<paramref name="escaped"/>: they wriggled free).</summary>
    private void LetGoOfResident(PeerSession s, bool escaped, bool notify)
    {
        if (s.HoldingNpc is not { } name) return;
        s.HoldingNpc = null;
        if (s.StrangleProgress > 0 && notify) _session.SendProgress(s.Peer, "", 0);
        (s.Strangling, s.StrangleProgress) = (false, 0);
        if (notify) _session.SendGrabState(s.Peer, "", "");
        foreach (var city in _instances.Cities)
            if (city.Npcs.TryGetValue(name, out var npc) && npc.HeldBy == s.Peer)
            {
                npc.HeldBy = 0;
                npc.Brain.Released(s.Peer, escaped);
                if (notify) _session.SendNotice(s.Peer, escaped ? $"{npc.NetName} breaks free!" : $"You let go of {npc.NetName}.");
                GD.Print($"[server] {npc.NetName} {(escaped ? "breaks free from" : "is let go by")} '{s.Character?.Name}' " +
                         $"after {Now - s.HoldingSince:0.0} s");
            }
    }

    /// <summary>Each physics frame: held residents are pulled along; they try to escape; held long enough, they pass out.</summary>
    private void TickGrapples()
    {
        foreach (var s in _sessions.Values)
        {
            if (s.HoldingNpc is { } name) TickHolding(s, name);
            if (s.HeldByNpc is { } holder) TickHeld(s, holder);
        }
    }

    private void TickHolding(PeerSession s, string name)
    {
        if (_instances.FindPlayer(s.Peer) is not { Instance: { Kind: InstanceKinds.City } city } found
            || !city.Npcs.TryGetValue(name, out var npc) || npc.IsDown || npc.HeldBy != s.Peer)
        {
            LetGoOfResident(s, escaped: false, notify: true);
            return;
        }
        // In front of the player, facing the same way (an arm round them from behind), never through a wall.
        var p = found.Player.NetPosition;
        var yaw = found.Player.NetYaw;
        var forward = new Vector3(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw));
        var want = p + forward * Breaching.HoldDistance;
        var hit = found.Player.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(
            p + Vector3.Up, want + Vector3.Up, collisionMask: 1));
        if (hit.Count > 0) want = p + forward * Mathf.Max(0, p.DistanceTo((Vector3)hit["position"] - Vector3.Up) - 0.3f);
        want.Y = p.Y;
        npc.Position = npc.NetPosition = want;
        npc.NetYaw = yaw;
        npc.Rotation = new Vector3(0, yaw, 0);
        npc.Brain.MoveHeld(want.X, want.Z, yaw, p.Y);

        var now = Now;
        var dt = (float)GetPhysicsProcessDeltaTime();
        var grit = npc.Brain.Person.Grit;
        if (s.Strangling)
        {
            s.StrangleProgress += dt / Breaching.StrangleSeconds(grit, s.HoldingSurprised);
            s.LastViolentAt = now; // anyone who sees it sees an attack
        }
        else if (s.StrangleProgress > 0)
            s.StrangleProgress = Mathf.Max(0, s.StrangleProgress - Breaching.StrangleRecoveryPerSecond * dt);
        if (s.StrangleProgress >= 1)
        {
            s.HoldingNpc = null;
            (s.Strangling, s.StrangleProgress) = (false, 0);
            npc.HeldBy = 0;
            npc.Brain.Attacked(s.Peer, p.X, p.Z, p.Y); // they'll remember who it was
            npc.KnockOut(WakeSeconds);
            _session.SendGrabState(s.Peer, "", "");
            _session.SendProgress(s.Peer, "", 0);
            _session.SendNotice(s.Peer, $"{npc.NetName} goes limp.");
            GD.Print($"[server] '{s.Character!.Name}' strangles {npc.NetName} unconscious in city {city.Id} " +
                     $"(held {now - s.HoldingSince:0.0} s)");
            return;
        }
        if (s.StrangleProgress > 0 && now - s.LastStrangleSent >= 0.15)
        {
            s.LastStrangleSent = now;
            _session.SendProgress(s.Peer, s.Strangling ? "Strangling" : "Easing off", -s.StrangleProgress);
        }
        if (now >= s.NextEscapeRoll)
        {
            s.NextEscapeRoll = now + 1;
            var chance = Breaching.EscapeChancePerSecond(grit, s.HoldingSurprised);
            if (s.Strangling) chance = Breaching.EscapeWhileStrangled(chance, s.StrangleProgress);
            if (npc.Brain.Frozen) chance = 0; // frozen stiff
            if (_combatRandom.NextDouble() < chance) LetGoOfResident(s, escaped: true, notify: true);
            else if (!s.HoldingSurprised && !s.Strangling && _combatRandom.NextDouble() < 0.5)
                EmitNoise(city, new Noise(want.X, want.Z, Stealth.ShoutRadius, "shout", Severity.Crime, s.Peer, want.Y));
        }
    }

    // ------------------------------------------------------------------ residents holding players

    private void NpcGrab(ServerInstance city, CityNpc npc, long peer)
    {
        if (npc.IsDown || npc.Brain.Held || !city.Players.TryGetValue(peer, out var player)
            || !_sessions.TryGetValue(peer, out var s) || s.Health.IsDead)
        {
            npc.Brain.GrabResult(peer, false);
            return;
        }
        var d = new Vector2(player.NetPosition.X - npc.Position.X, player.NetPosition.Z - npc.Position.Z).Length();
        if (d > Breaching.GrabReach + 0.3f || Mathf.Abs(player.NetPosition.Y - npc.Position.Y) > 1.2f)
        {
            npc.Brain.GrabResult(peer, false);
            return;
        }
        if (s.HeldByNpc is { } other && city.Npcs.TryGetValue(other, out var current) && current != npc)
        {
            if (npc.Role != NpcRole.Police || current.Role == NpcRole.Police)
            {
                npc.Brain.GrabResult(peer, false);
                npc.Brain.StandBy(peer); // someone has them already
                return;
            }
            current.Holding = 0; // the police take over
            current.Brain.HandOver();
        }
        LetGoOfBody(s, notify: true);
        if (s.HoldingNpc != null) LetGoOfResident(s, escaped: true, notify: true);
        CancelPick(s, notify: true);
        s.HeldByNpc = npc.Name;
        s.Struggle = 0;
        npc.Holding = peer;
        npc.Brain.GrabResult(peer, true);
        _session.SendGrabState(peer, "held", npc.NetName);
        _session.SendNotice(peer, npc.Role == NpcRole.Police
            ? $"{npc.NetName} has you! Mash F to break free, or be arrested."
            : $"{npc.NetName} grabs you! Mash F to break free.");
        GD.Print($"[server] {npc.NetName} ({npc.Role}) grabs '{s.Character!.Name}' in city {city.Id}");
    }

    private void NpcRelease(CityNpc npc, string how, bool notify = true)
    {
        if (npc.Holding == 0) return;
        var peer = npc.Holding;
        npc.Holding = 0;
        if (!_sessions.TryGetValue(peer, out var s) || s.HeldByNpc != npc.Name) return;
        s.HeldByNpc = null;
        if (notify)
        {
            _session.SendGrabState(peer, "", "");
            _session.SendNotice(peer, $"{npc.NetName} lets you go.");
        }
        GD.Print($"[server] {npc.NetName} {how} '{s.Character?.Name}'");
    }

    /// <summary>Held: stay within the holder's reach (pulled back if not), and free if they go down.</summary>
    private void TickHeld(PeerSession s, string holderName)
    {
        if (_instances.FindPlayer(s.Peer) is not { Instance: { Kind: InstanceKinds.City } city } found
            || !city.Npcs.TryGetValue(holderName, out var npc) || npc.IsDown || npc.Holding != s.Peer)
        {
            if (_instances.FindPlayer(s.Peer)?.Instance is { } c && c.Npcs.TryGetValue(holderName, out var n) && n.Holding == s.Peer)
                n.Brain.LostGrip();
            s.HeldByNpc = null;
            _session.SendGrabState(s.Peer, "", "");
            _session.SendNotice(s.Peer, "You're free!");
            return;
        }
        var p = found.Player.NetPosition;
        var away = new Vector3(p.X - npc.Position.X, 0, p.Z - npc.Position.Z);
        if (away.Length() > 1.4f && Now - s.LastPulledAt > 0.3)
        {
            s.LastPulledAt = Now;
            PlacePlayer(found.Player, npc.Position + away.Normalized() * 0.9f);
        }
    }

    /// <summary>One press of F while held: closer to breaking free. Free is resisting.</summary>
    private void Struggle(PeerSession s, ServerInstance city)
    {
        if (s.HeldByNpc is not { } name || !city.Npcs.TryGetValue(name, out var npc)) return;
        s.Struggle += Breaching.StrugglePerPress(npc.Brain.Person.Grit);
        if (s.Struggle < 1)
        {
            _session.SendProgress(s.Peer, "Struggling", -s.Struggle); // negative: a fraction done, not a duration
            return;
        }
        s.HeldByNpc = null;
        npc.Holding = 0;
        s.LastViolentAt = Now; // witnesses see a fight
        npc.Brain.GripBroken(s.Peer);
        _session.SendGrabState(s.Peer, "", "");
        _session.SendProgress(s.Peer, "", 0);
        _session.SendNotice(s.Peer, "You break free!");
        GD.Print($"[server] '{s.Character!.Name}' breaks free from {npc.NetName} ({npc.Role})");
    }

    /// <summary>Police held a player long enough: they're arrested and escorted out of the city.</summary>
    private void ArrestPlayer(ServerInstance city, CityNpc npc, long peer)
    {
        if (!_sessions.TryGetValue(peer, out var s) || s.HeldByNpc != npc.Name) return;
        npc.Holding = 0;
        s.HeldByNpc = null;
        GD.Print($"[server] {npc.NetName} arrests '{s.Character!.Name}' in city {city.Id}");
        NotifyInstance(city, $"{s.Character.Name} was arrested by {npc.NetName}.", except: peer);
        _session.SendNotice(peer, $"Arrested by {npc.NetName}. You're escorted out of the city.");
        _session.SendGrabState(peer, "", "");
        SendToInstance(s, _instances.Hub);
    }

    // ------------------------------------------------------------------ breaking in

    private bool DoorInReach(PeerSession s, string interactionId, out ServerInstance city, out CityDoor door, out int parcel)
    {
        (city, door, parcel) = (null!, null!, -1);
        if (!CityDoor.TryParse(interactionId, out parcel) || !IsNear(s, interactionId, out city) || city.Doors is null) return false;
        if (city.Nodes.Level.GetNodeOrNull<CityDoor>($"Door{parcel}") is not { } d) return false;
        door = d;
        var p = _instances.FindPlayer(s.Peer)!.Value.Player.NetPosition;
        return DevSkipProximity || new Vector2(p.X - d.GlobalPosition.X, p.Z - d.GlobalPosition.Z).Length() <= Breaching.DoorReach + 0.6f;
    }

    private void KickDoor(PeerSession s, string interactionId)
    {
        if (!DoorInReach(s, interactionId, out var city, out var door, out var parcel)) return;
        var doors = city.Doors!;
        var p = _instances.FindPlayer(s.Peer)!.Value.Player.NetPosition;
        if (door.IsInside(p)) { _session.SendNotice(s.Peer, "It opens inward: kick it from outside."); return; }
        if (doors.IsOpen(parcel)) { _session.SendNotice(s.Peer, doors.IsBroken(parcel) ? "It's already broken." : "It's open."); return; }
        if (Now - s.LastKickAt < Breaching.KickCooldown) return;
        s.LastKickAt = Now;
        s.LastBreakingAt = Now;
        var type = city.City!.Parcels[parcel].Building;
        var strength = Breaching.KicksToBreak(type);
        var broke = doors.Kick(parcel, strength);
        var at = door.GlobalPosition;
        EmitNoise(city, new Noise(at.X, at.Z, broke ? Stealth.BreakRadius : Stealth.KickRadius, "door kicked", Severity.Crime, s.Peer));
        if (broke)
        {
            _session.SendNotice(s.Peer, "The door bursts open!");
            GD.Print($"[server] '{s.Character!.Name}' kicks in door {parcel} ({type}) in city {city.Id}");
        }
        else
        {
            var d = doors[parcel];
            foreach (var peer in city.Players.Keys) _session.SendDoorStates(peer, [parcel, d.Flags | 32]); // shudder
            _session.SendNotice(s.Peer, $"The door holds. ({doors.Kicks(parcel)}/{strength})");
            GD.Print($"[server] '{s.Character!.Name}' kicks door {parcel} ({doors.Kicks(parcel)}/{strength})");
        }
    }

    private void StartPick(PeerSession s, string interactionId)
    {
        if (s.Picking != null || !DoorInReach(s, interactionId, out var city, out var door, out var parcel)) return;
        var doors = city.Doors!;
        if (!doors.IsLocked(parcel)) { _session.SendNotice(s.Peer, "It isn't locked."); return; }
        if (s.Inventory is not { } inv || inv.CountOf(ItemCatalog.LockpickId) == 0)
        {
            _session.SendNotice(s.Peer, "You need a lockpick (or the key).");
            return;
        }
        var player = _instances.FindPlayer(s.Peer)!.Value.Player;
        if (door.IsInside(player.NetPosition)) return; // from inside it's a thumb-turn (plain L)
        var seconds = Breaching.PickSeconds(city.City!.Parcels[parcel].Building);
        s.Picking = (parcel, Now + seconds, player.NetPosition, s.Health.Current);
        _session.SendProgress(s.Peer, "Picking the lock", seconds);
        EmitNoise(city, new Noise(door.GlobalPosition.X, door.GlobalPosition.Z, Stealth.PickRadius, "lockpicking", Severity.Crime, s.Peer));
        GD.Print($"[server] '{s.Character!.Name}' starts picking door {parcel} ({seconds:0} s)");
    }

    private void CancelPick(PeerSession s, bool notify)
    {
        if (s.Picking == null) return;
        s.Picking = null;
        _session.SendProgress(s.Peer, "", 0);
        if (notify) GD.Print($"[server] '{s.Character?.Name}' stops picking");
    }

    /// <summary>Each physics frame: a lock being picked finishes (roll) or is interrupted (moved, hurt, door changed).</summary>
    private void TickPicks()
    {
        foreach (var s in _sessions.Values)
        {
            if (s.Picking is not { } pick) continue;
            if (_instances.FindPlayer(s.Peer) is not { Instance: { Kind: InstanceKinds.City, Doors: { } doors } city } found)
            {
                s.Picking = null;
                continue;
            }
            if (found.Player.NetPosition.DistanceTo(pick.At) > 0.75f || s.Health.Current < pick.Health || !doors.IsLocked(pick.Parcel)
                || s.HeldByNpc != null)
            {
                CancelPick(s, notify: true);
                _session.SendNotice(s.Peer, "Interrupted.");
                continue;
            }
            if (Now < pick.DoneAt) continue;
            s.Picking = null;
            _session.SendProgress(s.Peer, "", 0);
            s.LastBreakingAt = Now;
            if (Breaching.RollPick(_combatRandom))
            {
                doors.Unlock(pick.Parcel);
                _session.SendNotice(s.Peer, "Click. The lock gives.");
                GD.Print($"[server] '{s.Character!.Name}' picks the lock of door {pick.Parcel} in city {city.Id}");
            }
            else
            {
                var slot = s.Inventory!.FindItemInSlot(ItemCatalog.LockpickId);
                if (slot >= 0 && --s.Inventory.slots[slot].stack_size <= 0) s.Inventory.RemoveItem(slot);
                SaveInventory(s, ItemEventFor(s, ItemActions.Destroyed, ItemCatalog.LockpickId, 1, null, city, $"broke picking door {pick.Parcel}"));
                SendInventory(s);
                _session.SendNotice(s.Peer, "Snap! Your lockpick breaks.");
                GD.Print($"[server] '{s.Character!.Name}' breaks a lockpick on door {pick.Parcel}");
            }
        }
    }

    /// <summary>A slug through a deadbolt (from close enough): the lock is destroyed and the door stays unlocked for good.</summary>
    private void BreakDoorLock(PeerSession s, ServerInstance city, CityDoor door, float distance)
    {
        if (city.Doors is not { } doors) return;
        s.LastBreakingAt = Now;
        if (distance > Core.Combat.CombatRules.BreachRange)
        {
            _session.SendNotice(s.Peer, "Too far: the slug only dents the lock. Get closer.");
            return;
        }
        if (doors.BreakLock(door.ParcelId))
        {
            _session.SendNotice(s.Peer, "The slug blows the lock apart!");
            GD.Print($"[server] '{s.Character!.Name}' shoots out the lock of door {door.ParcelId} in city {city.Id} ({distance:0.0} m)");
        }
    }
}
