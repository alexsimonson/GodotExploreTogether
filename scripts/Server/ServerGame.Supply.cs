using System.Linq;
using ExploreTogether.Core.Items;
using ExploreTogether.Core.Persistence;
using Godot;

namespace ExploreTogether.Server;

/// <summary>
/// Two hub stations:
/// - The debug supply (only with --dev-supply; dev-run turns it on): every item, unlimited. Taking makes a fresh
///   copy (with its serial, logged "created", origin "debug supply"); dropping one of your items onto it deletes
///   that item (logged "destroyed"). Like the storage, every move needs the player at the chest.
/// - The locksmith: trades in every key the player carries (keys rarely fit anything once their city has closed)
///   for <see cref="CoinsPerKey"/> coins each.
/// </summary>
public partial class ServerGame
{
    public const string DebugSupplyId = "debug_supply";
    public const string LocksmithId = "locksmith";
    public const int CoinsPerKey = 5;
    private const int CoinId = 1;

    private static readonly bool DevSupply = CmdArgs.Has("dev-supply");

    private void OpenSupply(PeerSession s)
    {
        if (!DevSupply)
        {
            _session.SendNotice(s.Peer, "The debug supply is closed on this server (start it with --dev-supply).");
            return;
        }
        _session.SendSupply(s.Peer, DebugSupply.Quads());
        GD.Print($"[server] '{s.Character!.Name}' opened the debug supply");
    }

    private bool CanUseSupply(PeerSession s) =>
        DevSupply && IsNear(s, DebugSupplyId, out var instance) && instance == _instances.Hub;

    /// <summary>A drag involving the supply: take a fresh copy out of it, or delete an item by dropping it onto it.</summary>
    private void SupplyMove(PeerSession s, ItemContainer from, int fromSlot, ItemContainer to, int toSlot)
    {
        if (!CanUseSupply(s))
        {
            _session.SendNotice(s.Peer, "You need to be at the debug supply.");
            SendInventory(s);
            return;
        }
        var name = s.Character!.Name;
        if (to == ItemContainer.Supply && from is ItemContainer.Inventory or ItemContainer.Gear)
        {
            // Delete: the item is gone for good.
            if (SlotContainerLogic.Drop(s.Inventory!, s.Gear!, from, fromSlot) is { } gone)
            {
                SaveInventory(s, ItemEventFor(s, ItemActions.Destroyed, gone.ItemId, gone.Count, gone.Uid, detail: "deleted at the debug supply"));
                GD.Print($"[server] '{name}' deleted {ItemCatalog.NameOf(gone.ItemId)} x{gone.Count} at the debug supply");
            }
            SendInventory(s);
            return;
        }
        if (from != ItemContainer.Supply || DebugSupply.At(fromSlot) is not { } def
            || SlotContainerLogic.Slot(s.Inventory!, s.Gear!, to, toSlot) is not { } target
            || to is not (ItemContainer.Inventory or ItemContainer.Gear))
        {
            SendInventory(s);
            return;
        }
        // Into an empty slot (a gear slot of the item's own type), or onto a stack of the same item.
        var fits = to == ItemContainer.Gear
            ? target.itemId == null && def.Slot == Gear.TypeOf(toSlot)
            : target.itemId == null || target.itemId == def.Id && def.Stack;
        if (!fits)
        {
            SendInventory(s);
            return;
        }
        var count = DebugSupply.CountPerTake(def);
        var events = new System.Collections.Generic.List<ItemEvent>();
        long? uid = null;
        if (NeedsSerial(def.Id))
        {
            (var serial, var created) = RegisterNewItem(s, def.Id, "debug supply");
            uid = serial;
            events.Add(created);
        }
        else events.Add(ItemEventFor(s, ItemActions.Created, def.Id, count, null, detail: "debug supply"));
        if (target.itemId == def.Id) target.stack_size += count;
        else (target.itemId, target.stack_size, target.data, target.uid) = (def.Id, count, def.InitialData, uid);
        SaveInventory(s, events.ToArray());
        SendInventory(s);
        GD.Print($"[server] '{name}' took {def.Name}{(count > 1 ? $" x{count}" : "")} from the debug supply");
    }

    /// <summary>The locksmith buys every key the player carries.</summary>
    private void TradeInKeys(PeerSession s)
    {
        if (s.Inventory == null) return;
        var keys = s.Inventory.slots.Where(k => k.itemId == ItemCatalog.KeyId).ToList();
        if (keys.Count == 0)
        {
            _session.SendNotice(s.Peer, $"The locksmith buys old keys: {CoinsPerKey} coins each. You have none.");
            return;
        }
        var coins = keys.Count * CoinsPerKey;
        var events = keys.Select(k => ItemEventFor(s, ItemActions.Destroyed, ItemCatalog.KeyId, 1, k.uid, detail: "traded in at the locksmith"))
            .Append(ItemEventFor(s, ItemActions.Created, CoinId, coins, null, detail: "the locksmith, for keys"))
            .ToArray();
        // The keys go first, so their slots are free for the coins (which always fit then).
        foreach (var k in keys) (k.itemId, k.stack_size, k.data, k.uid) = (null, 0, null, null);
        s.Inventory.AddItems(CoinId, coins);
        SaveInventory(s, events);
        SendInventory(s);
        _session.SendNotice(s.Peer, $"The locksmith takes {keys.Count} key(s) for {coins} coins.");
        GD.Print($"[server] '{s.Character!.Name}' traded {keys.Count} key(s) for {coins} coins at the locksmith");
    }
}
