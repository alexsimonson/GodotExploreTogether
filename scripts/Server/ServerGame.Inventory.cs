using System.Collections.Generic;
using ExploreTogether.Core.Items;
using ExploreTogether.Core.Persistence;
using Godot;

namespace ExploreTogether.Server;

/// <summary>
/// Inventory and equipment requests from the client UI (port of the SlotContainer drag-and-drop
/// and DropItem). The client only asks; the server applies the original rules
/// (Core.Items.SlotContainerLogic), saves, and sends the resulting state back.
/// </summary>
public partial class ServerGame
{
    private void OnMoveItem(long peer, int fromContainer, int fromSlot, int toContainer, int toSlot)
    {
        if (!_sessions.TryGetValue(peer, out var s) || s.Inventory == null || s.Gear == null) return;
        if (!IsContainer(fromContainer) || !IsContainer(toContainer)) return;

        var weaponBefore = s.Gear.EquippedWeapon?.Id;
        var (from, to) = ((ItemContainer)fromContainer, (ItemContainer)toContainer);
        // What's in both slots before the move, for the log (a move into an occupied slot swaps the two).
        var moved = SlotSnapshot(s, from, fromSlot);
        var displaced = SlotSnapshot(s, to, toSlot);
        var changed = SlotContainerLogic.Move(s.Inventory, s.Gear, from, fromSlot, to, toSlot);

        // Always answer, so a rejected drag snaps back to the server's state on the client.
        SendInventory(s);
        if (!changed) return;

        // Log equipment changes (rearranging within one container isn't worth logging).
        if (from != to)
        {
            var (movedAction, displacedAction) = to == ItemContainer.Gear
                ? (ItemActions.Equipped, ItemActions.Unequipped)
                : (ItemActions.Unequipped, ItemActions.Equipped);
            if (moved is { } m) s.PendingItemEvents.Add(ItemEventFor(s, movedAction, m.ItemId, m.Count, m.Uid));
            if (displaced is { } d) s.PendingItemEvents.Add(ItemEventFor(s, displacedAction, d.ItemId, d.Count, d.Uid));
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

        var dropped = SlotContainerLogic.Drop(s.Inventory, s.Gear, (ItemContainer)container, slot);
        SendInventory(s);
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

    private static bool IsContainer(int c) => c is (int)ItemContainer.Inventory or (int)ItemContainer.Gear;

    private static (int ItemId, int Count, long? Uid)? SlotSnapshot(PeerSession s, ItemContainer container, int index) =>
        SlotContainerLogic.Slot(s.Inventory!, s.Gear!, container, index) is { itemId: { } id } slot
            ? (id, slot.stack_size, slot.uid)
            : null;
}
