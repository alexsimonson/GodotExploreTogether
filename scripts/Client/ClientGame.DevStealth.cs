using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ExploreTogether.City;
using ExploreTogether.Core.City;
using ExploreTogether.Core.Items;
using ExploreTogether.Net;
using ExploreTogether.Player;
using Godot;

namespace ExploreTogether.Client;

/// <summary>
/// Dev automation for breaking in and stealth (smoke scenarios "breach" and "stealth"). Players walk for real (the
/// server validates movement), along streets and through gates, like a person would:
///   --dev-breach     walk to three locked doors: kick the first in, pick the second (needs lockpicks), and shoot the
///                    third's lock out with a pump shotgun (needs one, and slugs: --dev-starter-kit)
///   --dev-trespass   walk into a building where someone is (an office or warehouse at work time) and stay put
///   --dev-grab       walk up behind a walking resident and grab them; hold on (--dev-strangle: and strangle them)
///   --dev-struggle   when a resident holds us, mash F to break free
/// </summary>
public partial class ClientGame
{
    private double _devStruggleTimer;
    private bool _devStrangling;

    private void DevStealthStart(int instanceId)
    {
        if (CmdArgs.Has("dev-breach")) GetTree().CreateTimer(2.5).Timeout += () => { if (_currentInstanceId == instanceId) _ = DevBreach(instanceId); };
        if (CmdArgs.Has("dev-trespass")) GetTree().CreateTimer(2.5).Timeout += () => { if (_currentInstanceId == instanceId) _ = DevTrespass(instanceId); };
        if (CmdArgs.Has("dev-grab")) GetTree().CreateTimer(2.5).Timeout += () => { if (_currentInstanceId == instanceId) _ = DevGrab(instanceId); };
    }

    private void DevStealthProcess(double delta)
    {
        if (!CmdArgs.Has("dev-struggle") || _grabState != "held") return;
        if ((_devStruggleTimer -= delta) > 0) return;
        _devStruggleTimer = 0.1;
        _session.SendCityAction("struggle", "");
    }

    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    /// <summary>Walks through the points in turn (giving up on one after a while). False if the player went away.</summary>
    private async Task<bool> DevWalk(IEnumerable<(float X, float Z)> points, int instanceId)
    {
        foreach (var (x, z) in points)
        {
            if (PlayerCharacter.Local is not { } me || _currentInstanceId != instanceId) return false;
            me.DevWalkTarget = new Vector3(x, 0, z);
            for (var t = 0.0; t < 20 && me.DevWalkTarget != null; t += 0.1)
            {
                await Wait(0.1);
                if (!IsInstanceValid(me) || _currentInstanceId != instanceId) return false;
            }
            me.DevWalkTarget = null;
        }
        return true;
    }

    /// <summary>The way from where we are (on a street) to just in front of a parcel's door.</summary>
    private static List<(float X, float Z)> DevWayToDoor(CityLayout city, CityPaths paths, Vector3 from, Parcel p)
    {
        var start = CityNav.NearestStreet(city, paths, (from.X, from.Z));
        var way = paths.StreetWalk(start, CityPaths.StreetOf(p), (0, 0));
        var gate = CityPaths.WayIn(p, p.Spots[0]);
        way.Add(gate[0]);
        way.Add(gate[1]);
        return way;
    }

    private (CityLayout City, CityPaths Paths) DevCity()
    {
        var city = CityLayout.Import(_cityLayoutData, CityCatalog.Catalog);
        return (city, new CityPaths(city));
    }

    private void FaceDoorLock(PlayerCharacter me, CityDoor door)
    {
        var at = door.LockPosition;
        me.LookAt(new Vector3(at.X, me.GlobalPosition.Y, at.Z));
    }

    // ---- --dev-breach ----

    private async Task DevBreach(int instanceId)
    {
        if (PlayerCharacter.Local is not { } me) return;
        var (city, paths) = DevCity();
        var here = CityNav.NearestStreet(city, paths, (me.GlobalPosition.X, me.GlobalPosition.Z));
        // The nearest locked, intact doors, by walking distance.
        var targets = city.Parcels
            .Where(p => _doorStates.TryGetValue(p.Id, out var d) && d.Locked && !d.Damaged)
            .OrderBy(p => paths.StreetRoute(here, CityPaths.StreetOf(p)).Count).Take(3).ToList();
        GD.Print($"[client] dev-breach: targets {string.Join(", ", targets.Select(t => $"{t.Building} {t.Id}"))}");
        if (targets.Count < 3) return;

        // 1. Kick the first one in.
        var kick = targets[0];
        if (!await DevWalk(DevWayToDoor(city, paths, me.GlobalPosition, kick), instanceId)) return;
        var kickDoor = DoorNode(kick.Id)!;
        for (var i = 0; i < 10 && !_doorStates[kick.Id].Broken; i++)
        {
            FaceDoorLock(me, kickDoor);
            _session.SendCityAction("kick", kickDoor.InteractionId);
            await Wait(1.0);
        }
        GD.Print(_doorStates[kick.Id].Broken ? $"[client] dev-breach: door {kick.Id} kicked in" : $"[client] dev-breach: door {kick.Id} held");

        // 2. Pick the second (a failure breaks a pick; try again).
        var pick = targets[1];
        if (!await DevWalk(DevWayToDoor(city, paths, me.GlobalPosition, pick), instanceId)) return;
        var pickDoor = DoorNode(pick.Id)!;
        for (var i = 0; i < 5 && _doorStates[pick.Id].Locked; i++)
        {
            FaceDoorLock(me, pickDoor);
            _session.SendLockDoor(pickDoor.InteractionId);
            await Wait(Breaching.PickSeconds(pick.Building) + 1.0);
        }
        GD.Print(!_doorStates[pick.Id].Locked ? $"[client] dev-breach: door {pick.Id} picked" : $"[client] dev-breach: door {pick.Id} still locked");

        // 3. Shoot the third one's lock out.
        var shoot = targets[2];
        EquipFromInventory(ItemCatalog.PumpShotgunId);
        if (!await DevWalk(DevWayToDoor(city, paths, me.GlobalPosition, shoot), instanceId)) return;
        var shootDoor = DoorNode(shoot.Id)!;
        await Wait(0.5);
        if (_magazine == 0) { _session.SendReload(); await Wait(0.5); }
        // Step back a little, then aim from the head at the deadbolt.
        FaceDoorLock(me, shootDoor);
        var head = me.GlobalPosition + new Vector3(0, 1.6f, 0);
        _session.SendAttack((shootDoor.LockPosition - head).Normalized());
        await Wait(1.0);
        GD.Print(_doorStates[shoot.Id].LockBroken ? $"[client] dev-breach: door {shoot.Id} lock shot out" : $"[client] dev-breach: door {shoot.Id} lock intact");
        // A shot-out door opens but won't lock.
        _session.SendInteract(shootDoor.InteractionId);
        await Wait(0.8);
        _session.SendInteract(shootDoor.InteractionId);
        await Wait(0.8);
        _session.SendLockDoor(shootDoor.InteractionId);
        await Wait(0.5);
        GD.Print("[client] dev-breach: done");
    }

    private void EquipFromInventory(int itemId)
    {
        for (var i = 0; i + 3 < _lastInventoryQuads.Length; i += 4)
            if (_lastInventoryQuads[i + 1] == itemId)
            {
                _session.SendMoveItem((int)ItemContainer.Inventory, _lastInventoryQuads[i], (int)ItemContainer.Gear, (int)EquipmentType.Weapon);
                return;
            }
    }

    // ---- --dev-trespass ----

    private async Task DevTrespass(int instanceId)
    {
        if (PlayerCharacter.Local is not { } me) return;
        var (city, paths) = DevCity();
        var npcs = _main.Instances.GetNodeOrNull(InstanceFactory.NodeName(instanceId))?.GetNode("Npcs").GetChildren().OfType<CityNpc>().ToList() ?? [];
        // A private building (not a shop) with someone in it and its door unlocked: walk in.
        var here = CityNav.NearestStreet(city, paths, (me.GlobalPosition.X, me.GlobalPosition.Z));
        var target = city.Parcels
            .Where(p => !p.IsPublic && _doorStates.TryGetValue(p.Id, out var d) && !d.Locked
                        && npcs.Any(n => !n.IsDown && p.Footprint.Contains(n.GlobalPosition.X, n.GlobalPosition.Z)))
            .OrderBy(p => paths.StreetRoute(here, CityPaths.StreetOf(p)).Count).FirstOrDefault();
        if (target == null)
        {
            GD.Print("[client] dev-trespass: no occupied building to walk into");
            return;
        }
        GD.Print($"[client] dev-trespass: walking into {target.Building} {target.Id}");
        if (!await DevWalk(DevWayToDoor(city, paths, me.GlobalPosition, target), instanceId)) return;
        if (_doorStates[target.Id] is { Open: false }) _session.SendInteract(CityDoor.IdFor(target.Id));
        await Wait(1.0);
        var inside = CityPaths.WayIn(target, target.Spots[0]);
        if (!await DevWalk([inside[2], target.Spots[0]], instanceId)) return;
        GD.Print($"[client] dev-trespass: inside {target.Building} {target.Id}");
    }

    // ---- --dev-grab ----

    private async Task DevGrab(int instanceId)
    {
        if (PlayerCharacter.Local is not { } me) return;
        var container = _main.Instances.GetNodeOrNull(InstanceFactory.NodeName(instanceId));
        var (city, paths) = DevCity();
        for (var t = 0.0; t < 60; t += 0.1)
        {
            if (!IsInstanceValid(me) || _currentInstanceId != instanceId) return;
            if (_grabState == "holding")
            {
                if (CmdArgs.Has("dev-strangle") && !_devStrangling)
                {
                    _devStrangling = true;
                    GD.Print("[client] dev-grab: strangling");
                    _session.SendCityAction("strangle", "1");
                }
                me.DevWalkTarget = null;
                await Wait(0.1);
                continue;
            }
            // Someone out on the street we can walk straight at (no fence or wall in between).
            var space = me.GetWorld3D().DirectSpaceState;
            bool Clear(Node3D n) => space.IntersectRay(PhysicsRayQueryParameters3D.Create(
                me.GlobalPosition + Vector3.Up * 0.5f, n.GlobalPosition + Vector3.Up * 0.5f, collisionMask: 1, exclude: [me.GetRid()])).Count == 0;
            // Prefer someone in a straight line; otherwise walk the streets toward the nearest passer-by.
            var walkers = container?.GetNodeOrNull("Npcs")?.GetChildren().OfType<CityNpc>()
                .Where(n => !n.IsDown && n.Role is NpcRole.Civilian or NpcRole.Worker && n.NetMoving
                            && CityPaths.IsOpenStreet(city, n.GlobalPosition.X, n.GlobalPosition.Z)).ToList() ?? [];
            var target = walkers.Where(n => n.GlobalPosition.DistanceTo(me.GlobalPosition) < 25 && Clear(n))
                .MinBy(n => n.GlobalPosition.DistanceTo(me.GlobalPosition));
            if (target == null && walkers.MinBy(n => n.GlobalPosition.DistanceTo(me.GlobalPosition)) is { } far)
            {
                var route = CityNav.Route(city, paths, (me.GlobalPosition.X, me.GlobalPosition.Z), (far.GlobalPosition.X, far.GlobalPosition.Z), (0, 0));
                var next = route.FirstOrDefault(p => new Vector2(p.X - me.GlobalPosition.X, p.Z - me.GlobalPosition.Z).Length() > 1);
                me.DevWalkTarget = new Vector3(next.X, 0, next.Z);
            }
            if (target == null && (int)(t * 10) % 50 == 0) GD.Print("[client] dev-grab: nobody in reach yet");
            if (target != null)
            {
                // Come at them from behind.
                var behind = target.GlobalPosition + new Vector3(Mathf.Sin(target.Rotation.Y), 0, Mathf.Cos(target.Rotation.Y)) * 0.9f;
                var d = me.GlobalPosition.DistanceTo(target.GlobalPosition);
                me.DevWalkTarget = d > 2.5f ? target.GlobalPosition : behind;
                if (d < Breaching.GrabReach)
                {
                    me.DevWalkTarget = null;
                    GD.Print($"[client] dev-grab: grabbing {target.NetName}");
                    _session.SendCityAction("grab", target.LootId);
                    await Wait(0.5);
                }
            }
            await Wait(0.1);
        }
    }
}
