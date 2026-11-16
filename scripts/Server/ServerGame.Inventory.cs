using System.Collections.Generic;
using ExploreTogether.Core.Items;
using ExploreTogether.Core.Persistence;
using Godot;

namespace ExploreTogether.Server;

/// <summary>
/// Inventory, equipment and storage requests from the client UI (port of the SlotContainer drag-and-drop
/// and DropItem). The client only asks; the server applies the original rules
/// (Core.Items.SlotContainerLogic), saves, and sends the resulting state back.
/// Anything touching the storage needs the player at the hub's storage chest (ServerGame.Storage).
/// </summary>
public partial class ServerGame
{
    private void OnMoveItem(long peer, int fromContainer, int fromSlot, int toContainer, int toSlot)
    {
        if (!_sessions.TryGetValue(peer, out var s) || s.Inventory == null || s.Gear == null) return;
        if (!IsContainer(fromContainer) || !IsContainer(toContainer)) return;

        var weaponBefore = s.Gear.EquippedWeapon?.Id;
        var (from, to) = ((ItemContainer)fromContainer, (ItemContainer)toContainer);
        var usesStorage = from == ItemContainer.Storage || to == ItemContainer.Storage;
        // What's in both slots before the move, for the log (a move into an occupied slot swaps the two).
        var moved = SlotSnapshot(s, from, fromSlot);
        var displaced = SlotSnapshot(s, to, toSlot);
        var changed = false;
        if (usesStorage && !CanUseStorage(s))
            _session.SendNotice(peer, "You need to be at the storage chest.");
        else if (to == ItemContainer.Storage && moved?.ItemId == ItemCatalog.DungeonPassId)
            _session.SendNotice(peer, "The Dungeon Pass belongs to the dungeon.");
        else
            changed = SlotContainerLogic.Move(s.Inventory, s.Gear, from, fromSlot, to, toSlot, s.Storage);

        // Always answer, so a rejected drag snaps back to the server's state on the client.
        SendInventory(s);
        if (usesStorage) SendStorage(s);
        if (!changed) return;

        // Log moves between containers (rearranging within one container isn't worth logging). If the target
        // held something and it ended up in the source slot, the two were swapped; otherwise they merged.
        if (from != to)
        {
            if (moved is { } m) s.PendingItemEvents.Add(ItemEventFor(s, MoveAction(from, to), m.ItemId, m.Count, m.Uid));
            if (displaced is { } d && SlotSnapshot(s, from, fromSlot) is not null)
                s.PendingItemEvents.Add(ItemEventFor(s, MoveAction(to, from), d.ItemId, d.Count, d.Uid));
        }
        // Not saved now: a move only rearranges what the character already owns, so losing it in a crash
        // costs nothing, and saving every drag let a spamming client stall the server (security review,
        // finding 2). The next save (pickup, drop, reload, the 15 s autosave, disconnect) writes it and its events.
        if (s.Gear.EquippedWeapon?.Id != weaponBefore)
            GD.Print($"[server] '{s.Character!.Name}' weapon: {s.Gear.EquippedWeapon?.Name ?? "none"}");
    }

    private void OnDropItem(long peer, int container, int slot)
    {
        if (!_sessions.TryGetValue(peer, out var s) || s.Inventory == null || s.Gear == null || !IsContainer(container)) return;
        if (_instances.FindPlayer(peer) is not { } found) return; // must be standing somewhere to drop
        var fromStorage = container == (int)ItemContainer.Storage;
        if (fromStorage && !CanUseStorage(s))
        {
            _session.SendNotice(peer, "You need to be at the storage chest.");
            SendStorage(s);
            return;
        }

        var dropped = SlotContainerLogic.Drop(s.Inventory, s.Gear, (ItemContainer)container, slot, s.Storage);
        SendInventory(s);
        if (fromStorage) SendStorage(s);
        if (dropped is not { } d) return;

        // Like the original: it lands at the player's position (a little in front and above, so it's visible).
        var forward = -found.Player.Transform.Basis.Z;
        var position = found.Player.NetPosition + forward * 0.8f + new Vector3(0, 0.8f, 0);
        _instances.SpawnItem(found.Instance, d.ItemId, position, d.Count, d.Data, d.Uid);
        if (d.ItemId == ItemCatalog.DungeonPassId) SaveInventory(s); // the pass isn't tracked
        else SaveInventory(s, ItemEventFor(s, ItemActions.Dropped, d.ItemId, d.Count, d.Uid, found.Instance));
        GD.Print($"[server] '{s.Character!.Name}' dropped {ItemCatalog.NameOf(d.ItemId)} x{d.Count} in instance {found.Instance.Id}");

        if (d.ItemId == ItemCatalog.DungeonPassId)
            BroadcastDungeonStatus(found.Instance);
    }

    private static bool IsContainer(int c) => c is (int)ItemContainer.Inventory or (int)ItemContainer.Gear or (int)ItemContainer.Storage;

    private static (int ItemId, int Count, long? Uid)? SlotSnapshot(PeerSession s, ItemContainer container, int index) =>
        SlotContainerLogic.Slot(s.Inventory!, s.Gear!, container, index, s.Storage) is { itemId: { } id } slot
            ? (id, slot.stack_size, slot.uid)
            : null;
}
