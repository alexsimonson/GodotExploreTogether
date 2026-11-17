using ExploreTogether.Core.Audio;
using System.Collections.Generic;
using System.Linq;
using ExploreTogether.Core.Items;
using ExploreTogether.Core.Persistence;
using ExploreTogether.Dungeon;
using ExploreTogether.Items;
using ExploreTogether.Net;
using Godot;

namespace ExploreTogether.Server;

/// <summary>
/// Dungeon gameplay rules (phase 4): item pickups, the shared Dungeon Pass, the exit, and run records.
///
/// - Items: anyone in the instance can pick one up (look + E); it goes into that player's inventory.
/// - The Dungeon Pass belongs to the dungeon: it's never saved, and if its holder leaves the dungeon
///   (to the hub, or by disconnecting) it drops where they stood, so the team can still use it.
/// - The exit starts locked. A player holding the pass unlocks it (the pass is used up); it then
///   stays open for everyone in the instance.
/// - Each player escapes on their own by using the open exit, which returns them to the hub.
/// - The run ends when the last player leaves: "completed" if anyone escaped, otherwise "abandoned".
/// </summary>
public partial class ServerGame
{
    private void PickUp(PeerSession s, ServerInstance instance, string interactionId)
    {
        var name = interactionId[ItemPickup.InteractionPrefix.Length..];
        if (!instance.Items.TryGetValue(name, out var item) || s.Inventory == null) return;

        // Fresh loot gets its serial on its first pickup (only once there's room for it). The ground item
        // keeps it from then on, so the serial is issued once whoever ends up with it.
        var events = new List<ItemEvent>();
        if (item.Uid == null && item.Count == 1 && NeedsSerial(item.ItemId) && s.Inventory.FindEmptySlot() >= 0)
        {
            var origin = instance.RunId is { } run ? $"loot, run {run}" : $"loot, instance {instance.Id}";
            (item.Uid, var created) = RegisterNewItem(s, item.ItemId, origin, instance);
            events.Add(created);
        }

        var added = s.Inventory.AddItems(item.ItemId, item.Count, item.Data, item.Uid);
        if (added == 0)
        {
            _session.SendNotice(s.Peer, "Your inventory is full.");
            return;
        }
        events.Add(ItemEventFor(s, ItemActions.PickedUp, item.ItemId, added, item.Uid, instance));
        var itemName = item.Count > 1 ? $"{ItemCatalog.NameOf(item.ItemId)} x{added}" : ItemCatalog.NameOf(item.ItemId);
        if (added < item.Count)
        {
            // Partly fit: leave the rest on the ground.
            _instances.SpawnItem(instance, item.ItemId, item.Position, item.Count - added, item.Data);
            _session.SendNotice(s.Peer, "Your inventory is full; some were left behind.");
        }
        var isPass = item.ItemId == ItemCatalog.DungeonPassId;
        PlaySound(instance, SoundId.ItemPickup, item.GlobalPosition); // before it's gone from the world
        _instances.RemoveItem(instance, item);
        SendInventory(s);
        if (!isPass) SaveInventory(s, events.ToArray()); // the pass is never saved (or tracked)

        GD.Print($"[server] '{s.Character!.Name}' picked up {itemName} in instance {instance.Id}");
        if (isPass)
        {
            NotifyInstance(instance, $"{s.Character.Name} found the Dungeon Pass!");
            BroadcastDungeonStatus(instance);
        }
        else
        {
            _session.SendNotice(s.Peer, $"Picked up {itemName}");
        }
    }

    private void UseExit(PeerSession s, ServerInstance instance)
    {
        if (!instance.ExitOpen)
        {
            var passSlot = s.Inventory!.FindItemInSlot(ItemCatalog.DungeonPassId);
            if (passSlot < 0)
            {
                _session.SendNotice(s.Peer, "The exit is locked. Find the Dungeon Pass.");
                return;
            }
            // Like the original Objective: the pass is used up when the exit opens.
            s.Inventory.RemoveItem(passSlot);
            SendInventory(s);
            instance.ExitOpen = true;
            if (InstanceFactory.FindInteractable(instance.Nodes.Container, DungeonExit.Id) is Node3D exit)
                PlaySound(instance, SoundId.ExitOpen, exit.GlobalPosition);
            GD.Print($"[server] '{s.Character!.Name}' opened the exit of instance {instance.Id}");
            NotifyInstance(instance, $"{s.Character.Name} opened the exit!");
            BroadcastDungeonStatus(instance);
            return;
        }

        // Escape: this player is done; everyone else must still reach the exit themselves.
        var name = s.Character!.Name;
        if (instance.RunId is { } runId) _db.MarkEscaped(runId, s.Character.Id);
        if (!instance.Escaped.Contains(name)) instance.Escaped.Add(name);
        var remaining = instance.Players.Count - 1;
        GD.Print($"[server] '{name}' escaped instance {instance.Id}; {remaining} still inside");

        _session.SendNotice(s.Peer, remaining > 0
            ? $"You escaped! {remaining} still inside."
            : "You escaped! Run complete.");
        foreach (var other in instance.Players.Keys.Where(p => p != s.Peer))
            _session.SendNotice(other, $"{name} escaped! {remaining} still inside.");

        SendToInstance(s, _instances.Hub);
    }

    /// <summary>Called before a peer leaves a dungeon instance (to the hub or by disconnecting).</summary>
    private void OnLeavingDungeon(PeerSession s, ServerInstance instance, bool connected)
    {
        // Drop the Dungeon Pass where the player stood, so the rest of the team can still use it.
        var passSlot = s.Inventory?.FindItemInSlot(ItemCatalog.DungeonPassId) ?? -1;
        if (passSlot >= 0 && _instances.FindPlayer(s.Peer) is { } found)
        {
            s.Inventory!.RemoveItem(passSlot);
            if (connected) SendInventory(s);
            var pos = found.Player.NetPosition + new Vector3(0, 0.8f, 0);
            _instances.SpawnItem(instance, ItemCatalog.DungeonPassId, pos);
            NotifyInstance(instance, $"{s.Character!.Name} left and dropped the Dungeon Pass.", except: s.Peer);
        }
    }

    /// <summary>Called after a peer has left a dungeon instance: updates the others, and ends the run when it empties.</summary>
    private void OnLeftDungeon(ServerInstance instance)
    {
        if (instance.Players.Count > 0)
        {
            BroadcastDungeonStatus(instance);
            return;
        }
        if (instance.RunId is not { } runId) return;
        var outcome = instance.Escaped.Count > 0 ? "completed" : "abandoned";
        _db.EndRun(runId, outcome);
        var (_, participants, escaped) = _db.GetRunSummary(runId);
        GD.Print($"[server] run {runId} (instance {instance.Id}) {outcome}: {escaped}/{participants} escaped");
        instance.RunId = null;
    }

    private void BroadcastDungeonStatus(ServerInstance instance)
    {
        if (instance.Kind != InstanceKinds.Dungeon) return;
        var holder = _sessions.Values.FirstOrDefault(o =>
            instance.Players.ContainsKey(o.Peer) && o.Inventory?.Contains(ItemCatalog.DungeonPassId) == true);
        var status = new DungeonStatus(instance.ExitOpen, holder?.Character?.Name ?? "",
            instance.Escaped.ToArray(), instance.Players.Count);
        foreach (var peer in instance.Players.Keys)
            _session.SendDungeonStatus(peer, status);
    }

    private void NotifyInstance(ServerInstance instance, string message, long except = 0)
    {
        foreach (var peer in instance.Players.Keys.Where(p => p != except))
            _session.SendNotice(peer, message);
    }

    private void SendInventory(PeerSession s)
    {
        if (s.Inventory == null) return;
        _session.SendInventory(s.Peer,
            s.Inventory.Occupied().SelectMany(o => new[] { o.Slot, o.ItemId, o.Count, o.Data ?? -1 }).ToArray(),
            s.Gear?.ExportGear() ?? [], s.Gear?.ExportGearData() ?? []);
        // What others see in their hands, and whether the flashlight is still lit (put away: off).
        if (s.Gear != null && _instances.FindPlayer(s.Peer) is { } found)
        {
            found.Player.NetWeapon = s.Gear.EquippedWeapon?.Id ?? -1;
            if (s.Gear.slots[(int)EquipmentType.Offhand].itemId != ItemCatalog.FlashlightId) s.FlashlightOn = false;
            found.Player.NetFlashlight = s.FlashlightOn;
        }
        if (s.Hotbar is { } hotbar)
        {
            var (ids, uids) = hotbar.Export();
            _session.SendHotbar(s.Peer, ids, uids);
        }
        SendAmmoState(s); // the equipped gun's loaded rounds and the rounds carried may have changed
    }

    /// <summary>
    /// Saves the inventory and gear, plus the item events describing the change, in one transaction (so a
    /// crash can't duplicate or lose an item moving between them, and the log always matches the save).
    /// Leaves out the Dungeon Pass (it belongs to the dungeon). The hub storage is saved in the same transaction.
    /// </summary>
    private void SaveInventory(PeerSession s, params ItemEvent[] events)
    {
        if (s.Inventory == null || s.Gear == null || s.Character == null) return;
        _db.SaveItems(s.Character.Id,
            s.Inventory.Occupied().Where(o => o.ItemId != ItemCatalog.DungeonPassId),
            s.Gear.Occupied(), s.PendingItemEvents.Concat(events).ToList(),
            s.Storage?.Occupied().Where(o => o.ItemId != ItemCatalog.DungeonPassId));
        s.PendingItemEvents.Clear(); // only once the save succeeded (SaveItems throws otherwise)
    }
}
