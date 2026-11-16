using System.Collections.Generic;
using System.Linq;
using ExploreTogether.City;
using ExploreTogether.Core.Items;
using ExploreTogether.Core.Persistence;
using ExploreTogether.Net;
using Godot;

namespace ExploreTogether.Server;

/// <summary>
/// City prototype: the City Gate in the hub takes a player to the city, and the gate in the city brings them back.
///
/// One city at a time per group: the gate joins a city that has players in it (or arriving), so friends end up together.
/// When nobody is in one, it generates a new city (a new seed each visit). Empty cities close after the same
/// timeout as dungeons.
///
/// Server args: --city-size N (tiles per side, default 5), --city-seed S (fixed seed, default random),
/// --city-loops P (extra streets in percent, default 15), --city-favor NAME (dev: place that building design wherever it fits),
/// --city-people N (residents, default about 1.6 per tile), --city-start-hour H (default 8), --city-day-minutes M
/// (real minutes per game day, default 48), --dev-log-npcs (log what the residents are doing every 5 s).
/// </summary>
public partial class ServerGame
{
    private static readonly int CitySize = CmdArgs.GetInt("city-size", 5);
    private static readonly int CityLoops = CmdArgs.GetInt("city-loops", 15);
    private static readonly int? CitySeed = CmdArgs.Has("city-seed") ? CmdArgs.GetInt("city-seed", 0) : null;
    private static readonly string? CityFavor = CmdArgs.Has("city-favor") ? CmdArgs.Get("city-favor") : null;
    private static readonly int? CityPeople = CmdArgs.Has("city-people") ? CmdArgs.GetInt("city-people", 0) : null;
    private static readonly float CityStartHour = CmdArgs.GetFloat("city-start-hour", 8);
    private static readonly float CityDayMinutes = CmdArgs.GetFloat("city-day-minutes", 48);

    private static readonly int DevKillNpcs = CmdArgs.GetInt("dev-kill-npcs", 0);
    private static readonly int DevKnockOutNpcs = CmdArgs.GetInt("dev-knockout-npcs", 0);
    /// <summary>How long a knocked-out resident stays down (real seconds; --city-wake-seconds).</summary>
    private static readonly float WakeSeconds = CmdArgs.GetFloat("city-wake-seconds", 90);

    /// <summary>A player has arrived in a city: tell them the time and the state of every door.</summary>
    private void OnEnteredCity(PeerSession s, ServerInstance city)
    {
        if (city.Simulation is { } sim)
            _session.SendCityClock(s.Peer, sim.Clock.HourOfDay, sim.Clock.HoursPerSecond);
        if (city.Doors is { } doors)
            _session.SendDoorStates(s.Peer, doors.Export());
    }

    // ------------------------------------------------------------------ doors

    /// <summary>E on a door: open or close it. A locked door stays shut.</summary>
    private void UseDoor(PeerSession s, ServerInstance city, int parcel)
    {
        if (city.Doors is not { } doors || parcel < 0 || parcel >= doors.Count) return;
        var name = s.Character!.Name;
        if (doors.IsOpen(parcel))
        {
            doors.Close(parcel);
            GD.Print($"[server] door {parcel} closed by '{name}'");
        }
        else if (doors.IsLocked(parcel))
        {
            var hasKey = s.Inventory != null && doors.HasKey(s.Inventory, parcel);
            _session.SendNotice(s.Peer, hasKey ? "It's locked. Press L to unlock it." : "It's locked.");
            GD.Print($"[server] '{name}' tried locked door {parcel}");
        }
        else
        {
            doors.Open(parcel);
            GD.Print($"[server] door {parcel} opened by '{name}'");
        }
    }

    /// <summary>L on a door: lock or unlock it (closed doors only). From outside it takes the key; from inside, no key needed.</summary>
    private void OnLockDoor(long peer, string interactionId)
    {
        if (!_sessions.TryGetValue(peer, out var s) || !CityDoor.TryParse(interactionId, out var parcel)) return;
        if (!IsNear(s, interactionId, out var city) || city.Doors is not { } doors || _instances.FindPlayer(peer) is not { } found) return;
        if (city.Nodes.Level.GetNodeOrNull<CityDoor>($"Door{parcel}") is not { } door) return;
        if (doors.IsOpen(parcel))
        {
            _session.SendNotice(peer, "Close the door first.");
            return;
        }
        var inside = door.IsInside(found.Player.NetPosition);
        if (!inside && (s.Inventory == null || !doors.HasKey(s.Inventory, parcel)))
        {
            _session.SendNotice(peer, "You don't have the key to this door.");
            GD.Print($"[server] '{s.Character!.Name}' has no key to door {parcel}");
            return;
        }
        var how = inside ? "from inside" : "with a key";
        if (doors.IsLocked(parcel))
        {
            doors.Unlock(parcel);
            _session.SendNotice(peer, "Unlocked.");
            GD.Print($"[server] door {parcel} unlocked by '{s.Character!.Name}' ({how})");
        }
        else
        {
            doors.Lock(parcel);
            _session.SendNotice(peer, "Locked.");
            GD.Print($"[server] door {parcel} locked by '{s.Character!.Name}' ({how})");
        }
    }

    // ------------------------------------------------------------------ dragging bodies

    /// <summary>G: take hold of a body (dead or knocked out), or let go ("").</summary>
    private void OnDragBody(long peer, string interactionId)
    {
        if (!_sessions.TryGetValue(peer, out var s)) return;
        if (interactionId == "" || s.DraggingNpc != null)
        {
            LetGoOfBody(s, notify: true);
            if (interactionId == "") return;
        }
        if (!interactionId.StartsWith(CityNpc.LootPrefix) || !IsNear(s, interactionId, out var city)) return;
        if (!city.Npcs.TryGetValue(interactionId[CityNpc.LootPrefix.Length..], out var npc) || !npc.IsDown) return;
        if (npc.DraggedBy != 0 && npc.DraggedBy != peer && _sessions.TryGetValue(npc.DraggedBy, out var holder))
        {
            // Fighting over a body: grabbing it yanks it out of the other player's hands.
            LetGoOfBody(holder, notify: true);
            _session.SendNotice(holder.Peer, $"{s.Character!.Name} yanks {npc.NetName} away from you!");
            _session.SendNotice(peer, $"You yank {npc.NetName} away from {holder.Character?.Name}!");
            GD.Print($"[server] '{s.Character.Name}' yanks {npc.NetName} away from '{holder.Character?.Name}'");
        }
        npc.DraggedBy = peer;
        s.DraggingNpc = npc.Name;
        s.DragStart = npc.Position;
        _session.SendDragState(peer, npc.NetName);
        GD.Print($"[server] '{s.Character!.Name}' starts dragging {npc.NetName} ({(npc.NetDead ? "dead" : "unconscious")}) in city {city.Id}");
    }

    /// <summary>Each physics frame: bodies being dragged follow their draggers (never through walls, fences or closed doors).</summary>
    private void TickDrags()
    {
        foreach (var s in _sessions.Values)
        {
            if (s.DraggingNpc is not { } name) continue;
            if (_instances.FindPlayer(s.Peer) is not { Instance: { Kind: InstanceKinds.City } city } found
                || !city.Npcs.TryGetValue(name, out var npc) || !npc.IsDown)
            {
                LetGoOfBody(s, notify: true);
                continue;
            }
            var player = found.Player.NetPosition;
            var (x, z, yaw) = Core.City.BodyDrag.Follow(npc.Position.X, npc.Position.Z, player.X, player.Z);
            // Keep the body on the player's side of any wall: a ray from the player to where it would go.
            var from = new Vector3(player.X, 0.3f, player.Z);
            var to = new Vector3(x, 0.3f, z);
            var hit = found.Player.GetWorld3D().DirectSpaceState.IntersectRay(
                PhysicsRayQueryParameters3D.Create(from, to, collisionMask: 1));
            if (hit.Count > 0)
            {
                var stop = from.DistanceTo((Vector3)hit["position"]) - 0.3f;
                var dir = (to - from).Normalized();
                (x, z) = (from.X + dir.X * Mathf.Max(0, stop), from.Z + dir.Z * Mathf.Max(0, stop));
            }
            var position = new Vector3(x, 0, z);
            if (position.IsEqualApprox(npc.Position) && Mathf.IsEqualApprox(yaw, npc.NetYaw)) continue;
            npc.Position = npc.NetPosition = position;
            npc.NetYaw = yaw;
            npc.Rotation = new Vector3(0, yaw, 0);
            npc.Brain.MoveBody(x, z, yaw);
        }
    }

    /// <summary>Lets go of the body this player drags (if any).</summary>
    private void LetGoOfBody(PeerSession s, bool notify)
    {
        if (s.DraggingNpc is not { } name) return;
        s.DraggingNpc = null;
        foreach (var city in _instances.Cities)
            if (city.Npcs.TryGetValue(name, out var npc) && npc.DraggedBy == s.Peer)
            {
                npc.DraggedBy = 0;
                GD.Print($"[server] '{s.Character?.Name}' lets go of {npc.NetName}, " +
                         $"{s.DragStart.DistanceTo(npc.Position):0.0} m from where they picked them up");
            }
        if (notify) _session.SendDragState(s.Peer, "");
    }

    private void OnResidentWokeUp(CityNpc npc)
    {
        GD.Print($"[server] {npc.NetName} comes round");
        foreach (var s in _sessions.Values)
        {
            if (s.DraggingNpc == npc.Name)
            {
                LetGoOfBody(s, notify: true);
                _session.SendNotice(s.Peer, $"{npc.NetName} comes round and pulls free!");
            }
            if (s.LootingNpc == npc.Name) s.LootingNpc = null;
        }
    }

    // ------------------------------------------------------------------ residents: harm and loot

    /// <summary>
    /// A player's attack reached a resident. A blunt weapon knocks a standing resident out; anything else hurts
    /// (and can kill someone already knocked out). Returns true (it hit someone, standing or not).
    /// </summary>
    private bool HitResident(PeerSession s, ServerInstance city, CityNpc npc, int damage, string weaponName, bool knocksOut)
    {
        if (npc.NetDead) return true; // the body takes the hit
        if (knocksOut)
        {
            if (npc.KnockOut(WakeSeconds))
                GD.Print($"[server] '{s.Character!.Name}' knocked out {npc.NetName} ({npc.Role}) with {weaponName} in city {city.Id}");
            return true;
        }
        var killed = npc.TakeDamage(damage);
        GD.Print($"[server] '{s.Character!.Name}' hit {npc.NetName} ({npc.Role}) with {weaponName} for {damage} " +
                 $"({npc.NetHealth} left){(killed ? $", killed in city {city.Id}" : "")}");
        return true;
    }

    /// <summary>E on a body: show what it carries (take-only).</summary>
    private void SearchBody(PeerSession s, ServerInstance city, string npcName)
    {
        if (!city.Npcs.TryGetValue(npcName, out var npc) || !npc.IsDown) return;
        s.LootingNpc = npcName;
        SendLoot(s, npc);
        GD.Print($"[server] '{s.Character!.Name}' searches the body of {npc.NetName} ({ItemsOn(npc)} item(s))");
    }

    private static int ItemsOn(CityNpc npc) => npc.Inventory.Occupied().Count() + npc.Gear.Occupied().Count();

    private void SendLoot(PeerSession s, CityNpc npc) =>
        _session.SendLoot(s.Peer, npc.NetName,
            npc.Inventory.Occupied().SelectMany(o => new[] { o.Slot, o.ItemId, o.Count, o.Data ?? -1 }).ToArray(),
            npc.Gear.ExportGear(), npc.Gear.ExportGearData());

    /// <summary>The body this player is searching, if they're still at it.</summary>
    private bool CanSearch(PeerSession s, out ServerInstance city, out CityNpc npc)
    {
        city = null!;
        npc = null!;
        if (s.LootingNpc is not { } name || _instances.FindPlayer(s.Peer)?.Instance is not { Kind: InstanceKinds.City } c) return false;
        if (!c.Npcs.TryGetValue(name, out var n) || !n.IsDown || !IsNear(s, n.LootId, out _)) return false;
        (city, npc) = (c, n);
        return true;
    }

    /// <summary>
    /// A drag involving a body's loot: only taking is allowed, into an empty slot (or onto the same stackable).
    /// What's taken becomes the player's like a pickup: non-stackables get their serial now, and it's saved at once.
    /// </summary>
    private void TakeFromBody(PeerSession s, ItemContainer from, int fromSlot, ItemContainer to, int toSlot)
    {
        if (!CanSearch(s, out var city, out var npc))
        {
            _session.SendNotice(s.Peer, "You need to be at the body.");
            SendInventory(s);
            return;
        }
        var (loot, lootGear) = (npc.Inventory, npc.Gear);
        var allowed = SlotContainerLogic.IsAllowedForLoot(s.Inventory!, s.Gear!, loot, from, fromSlot, to, toSlot, lootGear);
        var source = SlotContainerLogic.Slot(s.Inventory!, s.Gear!, from, fromSlot, loot: loot, lootGear: lootGear);
        var (itemId, count) = (source?.itemId, source?.stack_size ?? 0);
        if (to is ItemContainer.Loot or ItemContainer.LootGear) _session.SendNotice(s.Peer, "You can only take from a body.");
        if (!allowed || itemId is not { } id
            || !SlotContainerLogic.Move(s.Inventory!, s.Gear!, from, fromSlot, to, toSlot, loot: loot, lootGear: lootGear))
        {
            SendInventory(s);
            SendLoot(s, npc);
            return;
        }

        var target = SlotContainerLogic.Slot(s.Inventory!, s.Gear!, to, toSlot)!;
        var origin = $"from the body of {npc.NetName}";
        var events = new List<ItemEvent>();
        if (target.uid == null && NeedsSerial(id))
        {
            (var uid, var created) = RegisterNewItem(s, id, $"taken {origin}, city {city.Id}", city);
            target.uid = uid;
            events.Add(created);
        }
        events.Add(ItemEventFor(s, ItemActions.PickedUp, id, count, target.uid, city, origin));
        SaveInventory(s, events.ToArray());
        SendInventory(s);
        SendLoot(s, npc);
        GD.Print($"[server] '{s.Character!.Name}' took {ItemCatalog.NameOf(id)}{(count > 1 ? $" x{count}" : "")} {origin}");
    }

    private ServerInstance CreateCity(string createdBy)
    {
        var city = _instances.CreateCity(CitySize, CitySize, CitySeed, CityLoops, createdBy, CityFavor,
            CityPeople, CityStartHour, CityDayMinutes);
        // Everyone in the city sees doors open, close, lock and unlock.
        city.Doors!.Changed += parcel =>
        {
            var d = city.Doors[parcel];
            foreach (var peer in city.Players.Keys)
                _session.SendDoorStates(peer, [parcel, d.Open ? 1 : 0, d.Locked ? 1 : 0]);
        };
        // Someone who comes round can't still be dragged or searched.
        foreach (var npc in city.Npcs.Values)
            npc.WokeUp += OnResidentWokeUp;
        // Dev (--dev-kill-npcs N, --dev-knockout-npcs N): bodies to search or drag right away, preferably on the street.
        var onStreet = city.Npcs.Values.OrderBy(n => n.Brain.Inside >= 0).ThenBy(n => n.NetId).ToList();
        foreach (var npc in onStreet.Take(DevKillNpcs))
        {
            npc.TakeDamage(CityNpc.MaxHealth);
            GD.Print($"[server] dev: {npc.NetName} ({npc.Role}) dies in city {city.Id}, carrying {ItemsOn(npc)} item(s)");
        }
        foreach (var npc in onStreet.Skip(DevKillNpcs).Take(DevKnockOutNpcs))
        {
            npc.KnockOut(WakeSeconds);
            GD.Print($"[server] dev: {npc.NetName} ({npc.Role}) is knocked out in city {city.Id} for {WakeSeconds:0} s");
        }
        // Keep everyone's clock in step (clients run it on between updates).
        city.Simulation!.ClockBroadcast += (hour, rate) =>
        {
            foreach (var peer in city.Players.Keys) _session.SendCityClock(peer, hour, rate);
        };
        return city;
    }

    private void UseCityGate(PeerSession s, ServerInstance instance)
    {
        if (instance == _instances.Hub)
        {
            // A city counts as in use while someone is in it or still loading into it.
            var city = _instances.Cities.FirstOrDefault(c =>
                           c.Players.Count > 0 || _sessions.Values.Any(o => o.PendingInstanceId == c.Id))
                       ?? CreateCity(s.Character!.Name);
            Save(s, CityReturnPoint); // a later login starts in front of the gate, not inside it
            GD.Print($"[server] '{s.Character!.Name}' goes to city {city.Id}");
            SendToInstance(s, city);
        }
        else if (instance.Kind == InstanceKinds.City)
        {
            GD.Print($"[server] '{s.Character!.Name}' returns to the hub from city {instance.Id}");
            SendToInstance(s, _instances.Hub);
        }
    }
}
