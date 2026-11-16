using System.Collections.Generic;
using System.Linq;
using ExploreTogether.City;
using ExploreTogether.Core.City;
using ExploreTogether.Core.Items;
using ExploreTogether.Dungeon;
using ExploreTogether.Net;
using ExploreTogether.Player;
using Godot;

namespace ExploreTogether.Client;

/// <summary>
/// Client side of the city:
/// - The clock in the top-right corner, and daylight that follows it. The server sends the clock on arrival and
///   every few seconds; in between, the client runs it on at the same rate.
/// - Doors: the server sends every door's state on arrival, then each change; doors swing to match.
/// - Searching a body: E on a dead resident opens the loot panel next to the inventory (take-only, "Take all").
///   Walking away closes it.
///
/// - Dragging: G on someone who's down (dead or knocked out) takes hold of them; G again lets go. The server moves
///   the body; the player walks slowly meanwhile (no sprinting or jumping).
///
/// Dev automation (needs the server's --dev-skip-proximity and --dev-kill-npcs N / --dev-knockout-npcs N):
///   --dev-doors   search a body, take everything, then lock and unlock the door their key fits, open and close
///                 it, and try a door without its key
///   --dev-drag    take hold of the nearest body, walk straight ahead for 4 s, and let go
/// </summary>
public partial class ClientGame
{
    private float _cityHour = -1, _cityHoursPerSecond;
    private int[] _cityLayoutData = [];
    private string? _lootNpcName; // the body being searched (node name)
    private string _draggingName = ""; // whoever we're dragging (their name), or ""

    private void OnDragState(string name)
    {
        _draggingName = name;
        if (PlayerCharacter.Local is { } me) me.Dragging = name != "";
        if (name != "") ShowNotice($"Dragging {name}. [G] to let go.");
        GD.Print(name != "" ? $"[client] dragging {name}" : "[client] not dragging anyone");
        DevDragStep(name);
    }

    // ---- dev automation: --dev-drag ----

    private bool _devDragStarted;

    private void DevDragStart(int instanceId)
    {
        if (!CmdArgs.Has("dev-drag") || _devDragStarted || PlayerCharacter.Local is not { } me) return;
        _devDragStarted = true;
        var body = _main.Instances.GetNodeOrNull(InstanceFactory.NodeName(instanceId))?.GetNode("Npcs").GetChildren()
            .OfType<CityNpc>().Where(n => n.IsDown).MinBy(n => n.GlobalPosition.DistanceTo(me.GlobalPosition));
        if (body == null)
        {
            GD.Print("[client] dev-drag: nobody down to drag");
            return;
        }
        GD.Print($"[client] dev-drag: taking hold of {body.NetName} ({(body.NetDead ? "dead" : "unconscious")})");
        _session.SendDragBody(body.LootId);
    }

    private void DevDragStep(string name)
    {
        if (!CmdArgs.Has("dev-drag") || name == "" || PlayerCharacter.Local is not { } me) return;
        me.DevWalkForward = true;
        GetTree().CreateTimer(4.0).Timeout += () =>
        {
            me.DevWalkForward = false;
            GD.Print("[client] dev-drag: letting go");
            _session.SendDragBody("");
        };
    }

    private void OnCityClock(float hour, float hoursPerSecond)
    {
        _cityHour = hour;
        _cityHoursPerSecond = hoursPerSecond;
    }

    private void CityProcess(double delta)
    {
        if (_currentKind != InstanceKinds.City) return;
        LootProcess();
        if (_cityHour < 0) return;
        _cityHour = (_cityHour + (float)delta * _cityHoursPerSecond) % 24f;
        _statusLabel.Text = $"City time {CityClock.Format(_cityHour)}";
        _statusLabel.Visible = true;
        if (_currentInstanceId is { } id
            && _main.Instances.GetNodeOrNull<CityDaylight>($"{InstanceFactory.NodeName(id)}/Level/Daylight") is { } daylight)
            daylight.SetHour(_cityHour);
    }

    // ---- doors ----

    private CityDoor? DoorNode(int parcel) => _currentInstanceId is { } id
        ? _main.Instances.GetNodeOrNull<CityDoor>($"{InstanceFactory.NodeName(id)}/Level/Door{parcel}")
        : null;

    /// <summary>(parcel, open, locked) triples: every door on arrival (applied at once), then single changes (animated).</summary>
    private void OnDoorStates(int[] triples)
    {
        var single = triples.Length == 3;
        for (var i = 0; i + 2 < triples.Length; i += 3)
            DoorNode(triples[i])?.SetState(triples[i + 1] != 0, triples[i + 2] != 0, animate: single);
        if (single && CmdArgs.Has("dev-doors"))
            GD.Print($"[client] door {triples[0]}: {(triples[1] != 0 ? "open" : "closed")}, {(triples[2] != 0 ? "locked" : "unlocked")}");
        else if (!single)
            GD.Print($"[client] doors: {triples.Length / 3} ({Enumerable.Range(0, triples.Length / 3).Count(i => triples[i * 3 + 2] != 0)} locked)");
    }

    // ---- searching bodies ----

    private void OnLoot(string name, int[] quads, int[] gear, int[] gearData)
    {
        var worn = gear.Where(g => g >= 0).Select(ItemCatalog.NameOf);
        GD.Print($"[client] {name} carries: " + string.Join(", ", Enumerable.Range(0, quads.Length / 4)
            .Select(i => ItemCatalog.NameOf(quads[i * 4 + 1]) + (quads[i * 4 + 2] > 1 ? $" x{quads[i * 4 + 2]}" : "")))
            + $" | wears: {string.Join(", ", worn)}");
        (_lastLootQuads, _lastLootGear) = (quads, gear);
        if (_inventory.LootOpen) _inventory.UpdateLoot(quads, gear, gearData);
        else _inventory.ShowLoot(name, quads, gear, gearData);
        DevDoorsAfterLoot();
    }

    private int[] _lastLootQuads = [], _lastLootGear = [];

    /// <summary>Remember which body E was pressed on, so the panel can close when the player walks away.</summary>
    private void NoteInteraction(string interactionId)
    {
        if (interactionId.StartsWith(CityNpc.LootPrefix)) _lootNpcName = interactionId[CityNpc.LootPrefix.Length..];
    }

    private void LootProcess()
    {
        if (!_inventory.LootOpen) return;
        var body = _lootNpcName != null && _currentInstanceId is { } id
            ? _main.Instances.GetNodeOrNull<CityNpc>($"{InstanceFactory.NodeName(id)}/Npcs/{_lootNpcName}")
            : null;
        var near = body != null && PlayerCharacter.Local is { } me
                   && me.GlobalPosition.DistanceTo(body.GlobalPosition) <= Interactable.ServerMaxDistance;
        if (!near && !CmdArgs.Has("dev-doors")) _inventory.CloseLoot();
    }

    /// <summary>"Take all": what they wear, then what they carry, each into the stack it merges with or the next free inventory slot.</summary>
    private void TakeAllLoot()
    {
        var used = Enumerable.Range(0, _lastInventoryQuads.Length / 4).ToDictionary(i => _lastInventoryQuads[i * 4], i => _lastInventoryQuads[i * 4 + 1]);
        var free = new Queue<int>(Enumerable.Range(0, Inventory.DefaultMaxSlots).Where(i => !used.ContainsKey(i)));
        var items = Enumerable.Range(0, _lastLootGear.Length).Where(g => _lastLootGear[g] >= 0)
            .Select(g => (Container: ItemContainer.LootGear, Slot: g, Item: _lastLootGear[g]))
            .Concat(Enumerable.Range(0, _lastLootQuads.Length / 4)
                .Select(i => (Container: ItemContainer.Loot, Slot: _lastLootQuads[i * 4], Item: _lastLootQuads[i * 4 + 1])));
        foreach (var (container, slot, item) in items)
        {
            var stackOnto = ItemCatalog.Get(item) is { Stack: true } ? used.FirstOrDefault(u => u.Value == item).Key : -1;
            if (stackOnto < 0 || !used.ContainsKey(stackOnto) || used[stackOnto] != item) stackOnto = -1;
            var target = stackOnto >= 0 ? stackOnto : free.Count > 0 ? free.Dequeue() : -1;
            if (target < 0) break; // inventory full
            used[target] = item;
            _session.SendMoveItem((int)container, slot, (int)ItemContainer.Inventory, target);
        }
    }

    // ---- dev automation: --dev-doors ----

    private bool _devDoorsStarted, _devDoorsTook;

    private void DevDoorsStart(int instanceId)
    {
        if (!CmdArgs.Has("dev-doors") || _devDoorsStarted) return;
        _devDoorsStarted = true;
        var body = _main.Instances.GetNodeOrNull(InstanceFactory.NodeName(instanceId))?.GetNode("Npcs").GetChildren()
            .OfType<CityNpc>().FirstOrDefault(n => n.NetDead);
        if (body == null)
        {
            GD.Print("[client] dev-doors: no body to search");
            return;
        }
        _lootNpcName = body.Name;
        GD.Print($"[client] dev-doors: searching the body of {body.NetName}");
        _session.SendInteract(body.LootId);
    }

    private async void DevDoorsAfterLoot()
    {
        if (!CmdArgs.Has("dev-doors") || _devDoorsTook) return;
        _devDoorsTook = true;
        TakeAllLoot();
        await ToSignal(GetTree().CreateTimer(1.5), SceneTreeTimer.SignalName.Timeout);

        var city = CityLayout.Import(_cityLayoutData, CityCatalog.Catalog);
        var doors = new CityDoors(city);
        var keyParcels = Enumerable.Range(0, _lastInventoryQuads.Length / 4)
            .Where(i => _lastInventoryQuads[i * 4 + 1] == ItemCatalog.KeyId)
            .Select(i => doors.ParcelForKey(_lastInventoryQuads[i * 4 + 3])).Where(p => p >= 0).ToList();
        GD.Print($"[client] dev-doors: carrying keys to door(s) {string.Join(", ", keyParcels)}");
        if (keyParcels.Count == 0) return;
        var mine = keyParcels[0];
        async System.Threading.Tasks.Task Step(string what, System.Action act)
        {
            GD.Print($"[client] dev-doors: {what}");
            act();
            await ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout);
        }
        var door = DoorNode(mine);
        if (door?.IsOpen == true) await Step($"close door {mine}", () => _session.SendInteract(CityDoor.IdFor(mine)));
        await Step($"lock/unlock door {mine} with its key", () => _session.SendLockDoor(CityDoor.IdFor(mine)));
        await Step($"lock/unlock door {mine} again", () => _session.SendLockDoor(CityDoor.IdFor(mine)));
        if (DoorNode(mine)?.IsLocked == true)
            await Step($"unlock door {mine}", () => _session.SendLockDoor(CityDoor.IdFor(mine)));
        await Step($"open door {mine}", () => _session.SendInteract(CityDoor.IdFor(mine)));
        await Step($"close door {mine}", () => _session.SendInteract(CityDoor.IdFor(mine)));
        var other = Enumerable.Range(0, doors.Count).First(p => !keyParcels.Contains(p) && DoorNode(p) is { IsOpen: false });
        await Step($"try to lock/unlock door {other} without its key", () => _session.SendLockDoor(CityDoor.IdFor(other)));
        GD.Print("[client] dev-doors: done");
    }

    /// <summary>--dev-check-city: report the residents this client can see, and how many are walking.</summary>
    private void DevReportNpcs(int instanceId)
    {
        var npcs = _main.Instances.GetNodeOrNull(InstanceFactory.NodeName(instanceId))?.GetNode("Npcs").GetChildren()
            .OfType<CityNpc>().ToList() ?? [];
        var roles = string.Join(", ", npcs.GroupBy(n => n.Role).Select(g => $"{g.Count()} {g.Key}"));
        GD.Print($"[client] city: {npcs.Count} residents in view ({roles}), {npcs.Count(n => n.NetMoving)} walking, " +
                 $"clock {(_cityHour < 0 ? "not received" : CityClock.Format(_cityHour))}");
    }
}
