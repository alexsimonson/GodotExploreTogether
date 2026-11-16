using System.Collections.Generic;
using System.Linq;
using ExploreTogether.Core;
using ExploreTogether.Core.Items;
using ExploreTogether.Core.Persistence;
using Godot;

namespace ExploreTogether.Server;

/// <summary>
/// Item tracking, for detecting and tracing duplicated items (docs/ITEM-SECURITY-REVIEW-2026-11-15.md, finding 8):
/// - Serials: every copy of a non-stackable item (weapons, armor, ...) gets a serial from the database's
///   registry, which records where it came from. The serial travels with the copy (moves, drops, pickups,
///   saves). The same serial in two places means a duplicate. Loot gets its serial when first picked up;
///   copies saved before serials existed get one when their owner next loads. The Dungeon Pass is never
///   saved, so it isn't tracked.
/// - Event log: pickups, drops, equips, reloads, creations and destructions, saved in the same transaction
///   as the inventory they changed (see SaveInventory).
/// - Detection: an audit of the whole database at startup, and a check against the other online players
///   whenever a character loads.
/// - Tools (server args; print a report and quit): --audit-items, --item-history SERIAL, --item-log CHARACTER.
/// </summary>
public partial class ServerGame
{
    /// <summary>Whether copies of this item carry serials: non-stackables, except the Dungeon Pass.</summary>
    private static bool NeedsSerial(int itemId) =>
        itemId != ItemCatalog.DungeonPassId && ItemCatalog.Get(itemId) is { Stack: false };

    private static ItemEvent ItemEventFor(PeerSession s, string action, int itemId, int count, long? uid,
        ServerInstance? instance = null, string? detail = null) =>
        new(action, itemId, count, uid, s.Character?.Id, s.Character?.Name, instance?.Id, instance?.RunId, detail);

    /// <summary>Registers a new copy and returns its serial plus the matching "created" event (save them together).</summary>
    private (long Uid, ItemEvent Created) RegisterNewItem(PeerSession s, int itemId, string origin, ServerInstance? instance = null)
    {
        var uid = _db.RegisterItem(itemId, origin);
        return (uid, ItemEventFor(s, ItemActions.Created, itemId, 1, uid, instance, origin));
    }

    /// <summary>
    /// After a character's items are loaded: give a serial to any copy that lacks one (saved before serials
    /// existed), then compare its serials with everyone else online.
    /// </summary>
    private void OnCharacterItemsLoaded(PeerSession s)
    {
        if (s.Inventory == null || s.Gear == null) return;
        var events = new List<ItemEvent>();
        foreach (var slot in s.Inventory.slots.Concat(s.Gear.slots))
        {
            if (slot.itemId is not { } id || slot.uid != null || !NeedsSerial(id)) continue;
            slot.uid = _db.RegisterItem(id, "saved before item serials existed");
            events.Add(ItemEventFor(s, ItemActions.Registered, id, 1, slot.uid));
        }
        if (events.Count > 0)
        {
            SaveInventory(s, events.ToArray());
            GD.Print($"[server] gave serials to {events.Count} item(s) of '{s.Character!.Name}'");
        }
        ReportOnlineDuplicates(s);
    }

    /// <summary>Warns if a serial this player holds is also held by another online player.</summary>
    private void ReportOnlineDuplicates(PeerSession s)
    {
        var mine = Serials(s).ToDictionary(x => x.Uid, x => x.ItemId);
        foreach (var other in _sessions.Values.Where(o => o != s && o.Character != null))
            foreach (var (uid, itemId) in Serials(other))
                if (mine.ContainsKey(uid))
                    GD.PushWarning($"[server] DUPLICATE ITEM: serial {uid} ({ItemCatalog.NameOf(itemId)}) is held by both " +
                                   $"'{s.Character!.Name}' and '{other.Character!.Name}'. History: --item-history {uid}");
    }

    private static IEnumerable<(long Uid, int ItemId)> Serials(PeerSession s) =>
        (s.Inventory?.slots ?? []).Concat(s.Gear?.slots ?? [])
            .Where(x => x.uid != null && x.itemId != null).Select(x => (x.uid!.Value, x.itemId!.Value));

    /// <summary>Logs everything a character carried as destroyed (hardcore death), before the character is deleted.</summary>
    private void LogItemsDestroyed(PeerSession s, ServerInstance instance, string reason)
    {
        var events = (s.Inventory?.Occupied() ?? []).Concat(s.Gear?.Occupied() ?? [])
            .Where(r => r.ItemId != ItemCatalog.DungeonPassId)
            .Select(r => ItemEventFor(s, ItemActions.Destroyed, r.ItemId, r.Count, r.Uid, instance, reason))
            .ToList();
        if (events.Count > 0) _db.LogItemEvents(events);
    }

    /// <summary>Runs the whole-database audit at startup and reports what it finds.</summary>
    private void AuditItemsAtStartup()
    {
        var problems = _db.AuditItems();
        if (problems.Count == 0)
        {
            GD.Print("[server] item audit: no duplicated or unknown item serials");
            return;
        }
        GD.PushWarning($"[server] item audit found {problems.Count} problem(s); run the server with --audit-items for the full list");
        foreach (var p in problems.Take(10)) GD.PushWarning($"[server]   {Describe(p)}");
    }

    /// <summary>
    /// Command-line item tools. Returns true if one ran (the server then quits instead of starting):
    ///   --audit-items           list every duplicated, unregistered or mismatched serial (exit code 1 if any)
    ///   --item-history SERIAL   where a serial came from and everything logged about it
    ///   --item-log CHARACTER    a character's recent item events (works for deleted characters too)
    /// </summary>
    private bool RunItemToolIfRequested()
    {
        if (CmdArgs.Has("audit-items"))
        {
            var problems = _db.AuditItems();
            GD.Print(problems.Count == 0
                ? "[audit] no duplicated, unregistered or mismatched item serials"
                : $"[audit] {problems.Count} problem(s):");
            foreach (var p in problems)
            {
                GD.Print($"[audit] {Describe(p)}");
                foreach (var e in _db.GetItemHistory(p.Uid)) GD.Print($"[audit]     {Describe(e)}");
            }
            QuitAfterTool(problems.Count == 0 ? 0 : 1);
            return true;
        }
        if (CmdArgs.Has("item-history"))
        {
            if (!long.TryParse(CmdArgs.Get("item-history"), out var uid))
                GD.Print("[items] usage: --item-history SERIAL");
            else
            {
                var serial = _db.GetItemSerial(uid);
                GD.Print(serial is { } r
                    ? $"[items] serial {uid}: {ItemCatalog.NameOf(r.ItemId)}, created {r.CreatedAt} ({r.Origin})"
                    : $"[items] serial {uid} was never issued");
                foreach (var e in _db.GetItemHistory(uid)) GD.Print($"[items]   {Describe(e)}");
            }
            QuitAfterTool(0);
            return true;
        }
        if (CmdArgs.Has("item-log"))
        {
            var name = CmdArgs.Get("item-log");
            var events = _db.GetCharacterItemHistory(name);
            GD.Print($"[items] {events.Count} recent item event(s) for '{name}':");
            foreach (var e in events) GD.Print($"[items]   {Describe(e)}");
            QuitAfterTool(0);
            return true;
        }
        return false;
    }

    private void QuitAfterTool(int exitCode)
    {
        _db.Dispose();
        _db = null!; // nothing else was started, so there's nothing to save on exit
        GetTree().Quit(exitCode);
    }

    private static string Describe(ItemAuditProblem p) =>
        $"{p.Kind}: serial {p.Uid} ({ItemCatalog.NameOf(p.ItemId)}) at {string.Join("; ", p.Places)}";

    private static string Describe(ItemEventRecord r)
    {
        var e = r.Event;
        var what = e.Count > 1 ? $"{ItemCatalog.NameOf(e.ItemId)} x{e.Count}" : ItemCatalog.NameOf(e.ItemId);
        var serial = e.Uid is { } u ? $" #{u}" : "";
        var where = e.RunId is { } run ? $" in run {run}" : e.InstanceId is { } inst ? $" in instance {inst}" : "";
        var detail = e.Detail is { Length: > 0 } d ? $" ({d})" : "";
        return $"{r.At} {e.CharacterName ?? "-"} {e.Action} {what}{serial}{where}{detail}";
    }
}
