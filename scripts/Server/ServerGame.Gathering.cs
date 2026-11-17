using ExploreTogether.Core.Audio;
using System;
using System.Collections.Generic;
using System.Linq;
using ExploreTogether.Core.Gathering;
using ExploreTogether.Core.Items;
using ExploreTogether.Core.Persistence;
using ExploreTogether.Gathering;
using Godot;

namespace ExploreTogether.Server;

/// <summary>
/// Resource gathering (port of the original Resource.Interaction): a player with the right tool equipped
/// uses a resource (E, or a swing of the tool at it) and the server rolls for a drop with
/// Core.Gathering.ResourceRules. The drop goes straight into the inventory, like the original's
/// player_inventory.AddItem. Differences: a full inventory stops the attempt (the original lost the
/// item), the player is told what happened (the original only logged it), and there is a short cooldown.
/// </summary>
public partial class ServerGame
{
    private readonly Random _gatherRandom = new();

    private void Gather(PeerSession s, ServerInstance instance, string interactionId)
    {
        var name = interactionId[ResourceNode.InteractionPrefix.Length..];
        if (!instance.Resources.TryGetValue(name, out var resource) || s.Inventory == null || s.Gear == null) return;
        if (s.Health.IsDead) return;
        if (Now - s.LastGatherAt < ResourceRules.GatherCooldown) return;
        s.LastGatherAt = Now;

        var def = resource.Def;
        // Check for room before rolling, so a full inventory doesn't use up the resource.
        if (!resource.Depleted && ResourceRules.HasRightTool(def, s.Gear.EquippedWeapon) && !def.Drops.Any(s.Inventory.CanAdd))
        {
            _session.SendNotice(s.Peer, "Your inventory is full.");
            return;
        }

        var result = ResourceRules.Harvest(def, resource.Depleted, s.Gear.EquippedWeapon, _gatherRandom);
        switch (result.Outcome)
        {
            case HarvestOutcome.Depleted:
                _session.SendNotice(s.Peer, $"The {def.Name.ToLowerInvariant()} is depleted. Wait for it to grow back.");
                return;
            case HarvestOutcome.WrongTool:
                _session.SendNotice(s.Peer, $"You need {Article(def.RequiredRole)} equipped.");
                return;
            case HarvestOutcome.NothingFound:
                _session.SendNotice(s.Peer, "You get nothing this time.");
                return;
        }

        var itemId = result.ItemId!.Value;
        var origin = $"gathered from {def.Name}";
        var events = new List<ItemEvent>();
        long? uid = null;
        if (NeedsSerial(itemId))
        {
            (uid, var created) = RegisterNewItem(s, itemId, origin, instance);
            events.Add(created);
        }
        else
        {
            events.Add(ItemEventFor(s, ItemActions.Created, itemId, 1, null, instance, origin));
        }
        s.Inventory.AddItem(itemId, uid: uid);
        if (result.DepletesNow) resource.Deplete();

        SendInventory(s);
        SaveInventory(s, events.ToArray());
        if (resource is Node3D node) PlaySound(instance, SoundId.Chop, node.GlobalPosition + Vector3.Up);
        var total = s.Inventory.CountOf(itemId);
        _session.SendNotice(s.Peer, $"You get {ItemCatalog.NameOf(itemId)} ({total}).{(result.DepletesNow ? $" The {def.Name.ToLowerInvariant()} is depleted." : "")}");
        GD.Print($"[server] '{s.Character!.Name}' gathered {ItemCatalog.NameOf(itemId)} from {resource.Name} in instance {instance.Id}" +
                 $" (now {total}){(result.DepletesNow ? "; depleted" : "")}");
    }

    private static string Article(ToolRole role) => role switch
    {
        ToolRole.Axe => "an axe",
        _ => $"a {role.ToString().ToLowerInvariant()}",
    };
}
