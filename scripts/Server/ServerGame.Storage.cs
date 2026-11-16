using System.Linq;
using ExploreTogether.Core.Items;
using ExploreTogether.Core.Persistence;
using Godot;

namespace ExploreTogether.Server;

/// <summary>
/// Hub storage (port of the original Storage on the Hub prefab, saved per character as
/// CharacterData.hub_storage_inventory): a 50-slot container of the character's own, opened at the hub's
/// storage chest. Everyone uses the same chest but sees their own storage.
///
/// The server keeps no "open" state: every request that touches the storage (a move or a drop) must come
/// from a player standing at the chest, checked like any interaction. Moves follow the inventory's rules
/// (SlotContainerLogic) and are saved like other moves: with the next save, in the same transaction as the
/// inventory and gear, so an item can't be lost or duplicated moving between them.
/// </summary>
public partial class ServerGame
{
    private const string HubStorageId = "hub_storage";

    private void OpenStorage(PeerSession s)
    {
        if (s.Storage == null) return;
        SendStorage(s);
        GD.Print($"[server] '{s.Character!.Name}' opened their storage ({s.Storage.Occupied().Count()} slot(s) used)");
    }

    /// <summary>True if the player is standing at the hub's storage chest.</summary>
    private bool CanUseStorage(PeerSession s) =>
        s.Storage != null && IsNear(s, HubStorageId, out var instance) && instance == _instances.Hub;

    private void SendStorage(PeerSession s)
    {
        if (s.Storage == null) return;
        _session.SendStorage(s.Peer, s.Storage.Occupied().SelectMany(o => new[] { o.Slot, o.ItemId, o.Count, o.Data ?? -1 }).ToArray());
    }

    /// <summary>What to log for an item that moved from one container to another.</summary>
    private static string MoveAction(ItemContainer from, ItemContainer to) => (from, to) switch
    {
        (_, ItemContainer.Storage) => ItemActions.Stored,
        (ItemContainer.Storage, _) => ItemActions.Retrieved,
        (_, ItemContainer.Gear) => ItemActions.Equipped,
        _ => ItemActions.Unequipped,
    };
}
