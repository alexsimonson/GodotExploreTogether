using ExploreTogether.Core.Items;
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
        var changed = SlotContainerLogic.Move(s.Inventory, s.Gear,
            (ItemContainer)fromContainer, fromSlot, (ItemContainer)toContainer, toSlot);

        // Always answer, so a rejected drag snaps back to the server's state on the client.
        SendInventory(s);
        if (!changed) return;
        SaveInventory(s);
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
        _instances.SpawnItem(found.Instance, d.ItemId, position, d.Count);
        SaveInventory(s);
        GD.Print($"[server] '{s.Character!.Name}' dropped {ItemCatalog.NameOf(d.ItemId)} x{d.Count} in instance {found.Instance.Id}");

        if (d.ItemId == ItemCatalog.DungeonPassId)
            BroadcastDungeonStatus(found.Instance);
    }

    private static bool IsContainer(int c) => c is (int)ItemContainer.Inventory or (int)ItemContainer.Gear;
}
