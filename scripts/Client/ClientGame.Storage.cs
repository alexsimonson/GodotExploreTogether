using System.Collections.Generic;
using System.Linq;
using ExploreTogether.Core.Items;
using ExploreTogether.Dungeon;
using ExploreTogether.Gathering;
using ExploreTogether.Net;
using ExploreTogether.Player;
using Godot;

namespace ExploreTogether.Client;

/// <summary>
/// Client side of the hub storage and gathering:
/// - The server sends the storage when the player opens the chest (E) and after each change; the inventory
///   screen then shows it next to the inventory. Walking away from the chest closes the storage panel
///   (the server would refuse storage moves from there anyway).
/// - Gathering needs nothing special on the client: trees are interactables (E, or a swing of the axe).
///
/// Dev automation (needs the server's --dev-skip-proximity, and an axe: --dev-starter-kit + --dev-weapon Axe):
///   --dev-gather N   in the hub, chop the hub's trees until carrying N logs, then put them all into the storage
/// </summary>
public partial class ClientGame
{
    private const string HubStorageId = "hub_storage";

    private int[] _lastInventoryQuads = [];
    private readonly int _devGatherTarget = CmdArgs.GetInt("dev-gather", 0);
    private bool _devGatherStoring;
    private bool _devGatherDone;
    private double _devGatherTimer;
    private int _devGatherTree;

    private void SetUpStorageClient()
    {
        _session.StorageReceived += OnStorage;
        _session.SupplyReceived += OnSupply;
    }

    // ---- the debug supply and the locksmith (hub) ----

    private const string DebugSupplyId = "debug_supply", LocksmithId = "locksmith";
    private bool _devSupplyDone, _devTradeDone;

    private void OnSupply(int[] quads)
    {
        GD.Print($"[client] debug supply: {quads.Length / 4} items");
        if (_inventory.SupplyOpen || !NearHubStation(DebugSupplyId)) return;
        _inventory.ShowSupply(quads);
        DevSupplyTake();
    }

    private bool NearHubStation(string id)
    {
        if (_currentKind != InstanceKinds.Hub || PlayerCharacter.Local is not { } player) return false;
        if (CmdArgs.Has("dev-supply-take") || CmdArgs.Has("dev-trade-keys")) return true; // the automation doesn't walk over
        return InstanceFactory.FindInteractable(_main.Instances, id) is { } station
               && player.GlobalPosition.DistanceTo(station.GlobalPosition) <= Interactable.ServerMaxDistance;
    }

    /// <summary>
    /// Dev: --dev-supply-take NAME[,NAME...] opens the debug supply in the hub and takes those items;
    /// --dev-trade-keys trades the keys carried in at the locksmith (on reaching the hub carrying any).
    /// </summary>
    private void DevHubStations()
    {
        if (_currentKind != InstanceKinds.Hub) return;
        if (CmdArgs.Has("dev-supply-take") && !_devSupplyDone)
            GetTree().CreateTimer(1.0).Timeout += () => _session.SendInteract(DebugSupplyId);
        if (CmdArgs.Has("dev-trade-keys") && !_devTradeDone && CountInInventory(ItemCatalog.KeyId) > 0)
        {
            _devTradeDone = true;
            GetTree().CreateTimer(1.0).Timeout += () =>
            {
                GD.Print($"[client] dev: trading {CountInInventory(ItemCatalog.KeyId)} key(s) at the locksmith");
                _session.SendInteract(LocksmithId);
            };
        }
    }

    private void DevSupplyTake()
    {
        if (!CmdArgs.Has("dev-supply-take") || _devSupplyDone) return;
        _devSupplyDone = true;
        var used = Enumerable.Range(0, _lastInventoryQuads.Length / 4).Select(i => _lastInventoryQuads[i * 4]).ToHashSet();
        var free = new Queue<int>(Enumerable.Range(0, Inventory.DefaultMaxSlots).Where(i => !used.Contains(i)));
        foreach (var name in CmdArgs.Get("dev-supply-take").Split(','))
        {
            var slot = Enumerable.Range(0, DebugSupply.Items.Count).FirstOrDefault(i => DebugSupply.Items[i].Name == name, -1);
            if (slot < 0 || free.Count == 0) continue;
            GD.Print($"[client] dev: taking {name} from the debug supply");
            _session.SendMoveItem((int)ItemContainer.Supply, slot, (int)ItemContainer.Inventory, free.Dequeue());
        }
    }

    private void OnStorage(int[] quads)
    {
        var count = quads.Length / 4;
        GD.Print($"[client] storage ({count} slots used): " + string.Join(", ",
            Enumerable.Range(0, count).Select(i => ItemCatalog.NameOf(quads[i * 4 + 1]) + (quads[i * 4 + 2] > 1 ? $" x{quads[i * 4 + 2]}" : ""))));

        // Open it, unless this is the answer to a request made from too far away (then just keep it up to date).
        if (_inventory.StorageOpen || !NearStorage()) _inventory.UpdateStorage(quads);
        else _inventory.ShowStorage(quads);
        DevGatherStore(quads);
    }

    /// <summary>Called every frame: closes the storage panel once the player walks away from the chest.</summary>
    private void StorageProcess(double delta)
    {
        if (_inventory.StorageOpen && !NearStorage()) _inventory.CloseStorage();
        if (_inventory.SupplyOpen && !NearHubStation(DebugSupplyId)) _inventory.CloseSupply();
        DevGatherProcess(delta);
    }

    private bool NearStorage()
    {
        if (_currentKind != InstanceKinds.Hub || PlayerCharacter.Local is not { } player) return false;
        if (CmdArgs.Has("dev-gather")) return true; // the automation doesn't walk over (the server skips the check too)
        return InstanceFactory.FindInteractable(_main.Instances, HubStorageId) is { } chest
               && player.GlobalPosition.DistanceTo(chest.GlobalPosition) <= Interactable.ServerMaxDistance;
    }

    // ----------------------------------------------------- dev automation

    private void DevGatherProcess(double delta)
    {
        if (_devGatherTarget <= 0 || _devGatherDone || _devGatherStoring || _currentKind != InstanceKinds.Hub) return;
        if ((_devGatherTimer -= delta) > 0) return;
        _devGatherTimer = 0.7; // a little over the server's gather cooldown

        if (EquippedWeapon?.Class != ItemClass.Tool) return; // wait for --dev-weapon Axe
        if (CountInInventory(ItemCatalog.LogId) >= _devGatherTarget)
        {
            _devGatherStoring = true;
            GD.Print($"[client] dev-gather: carrying {CountInInventory(ItemCatalog.LogId)} logs; opening the storage");
            _session.SendInteract(HubStorageId);
            return;
        }
        // Take turns on the hub's trees, so a depleted one doesn't stall the run.
        var trees = _main.Instances.FindChildren("*", nameof(StaticBody3D), recursive: true, owned: false)
            .OfType<ResourceNode>().ToList();
        if (trees.Count == 0) return;
        var tree = trees[_devGatherTree++ % trees.Count];
        _session.SendInteract(tree.InteractionId);
    }

    private void DevGatherStore(int[] storageQuads)
    {
        if (!_devGatherStoring || _devGatherDone) return;
        _devGatherDone = true;
        var from = SlotOf(_lastInventoryQuads, ItemCatalog.LogId);
        if (from < 0) return;
        // Onto the logs already stored (they merge), otherwise into the first free slot.
        var to = SlotOf(storageQuads, ItemCatalog.LogId);
        if (to < 0)
        {
            var used = Enumerable.Range(0, storageQuads.Length / 4).Select(i => storageQuads[i * 4]).ToHashSet();
            to = Enumerable.Range(0, Inventory.HubStorageSlots).First(i => !used.Contains(i));
        }
        GD.Print($"[client] dev-gather: storing logs from inventory slot {from} into storage slot {to}");
        _session.SendMoveItem((int)ItemContainer.Inventory, from, (int)ItemContainer.Storage, to);
    }

    private int CountInInventory(int itemId) =>
        Enumerable.Range(0, _lastInventoryQuads.Length / 4).Where(i => _lastInventoryQuads[i * 4 + 1] == itemId)
            .Sum(i => _lastInventoryQuads[i * 4 + 2]);

    private static int SlotOf(int[] quads, int itemId)
    {
        for (int i = 0; i + 3 < quads.Length; i += 4)
            if (quads[i + 1] == itemId) return quads[i];
        return -1;
    }
}
