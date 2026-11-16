namespace ExploreTogether.Core.Persistence;

/// <summary>
/// One entry in the item event log (table item_events): something happened to an item.
/// Uid is the copy's serial for non-stackables; stackables (coins, ammo) are tracked by count only.
/// </summary>
public sealed record ItemEvent(
    string Action, int ItemId, int Count, long? Uid,
    long? CharacterId, string? CharacterName,
    int? InstanceId = null, long? RunId = null, string? Detail = null);

/// <summary>A logged event with its log id and UTC timestamp (ISO 8601).</summary>
public sealed record ItemEventRecord(long Id, string At, ItemEvent Event);

/// <summary>What the item event log records. Stored as text, so new actions need no migration.</summary>
public static class ItemActions
{
    /// <summary>A new copy came into existence (Detail: where from). Loot gets its serial on first pickup.</summary>
    public const string Created = "created";
    /// <summary>A copy saved before serials existed was given one when its owner logged in.</summary>
    public const string Registered = "registered";
    public const string PickedUp = "picked_up";
    public const string Dropped = "dropped";
    public const string Equipped = "equipped";
    public const string Unequipped = "unequipped";
    /// <summary>Ammo moved into a gun (ItemId = the ammo, Count = rounds; Detail names the gun's serial).</summary>
    public const string Loaded = "loaded";
    /// <summary>Gone for good, e.g. carried by a hardcore character who died.</summary>
    public const string Destroyed = "destroyed";
}

public enum ItemAuditKind
{
    /// <summary>The same serial is saved in more than one place.</summary>
    Duplicate,
    /// <summary>A serial the registry never issued.</summary>
    Unregistered,
    /// <summary>A serial issued for a different item than the one carrying it.</summary>
    WrongItem,
}

/// <summary>A problem found by <see cref="GameDatabase.AuditItems"/>: which serial, which item, and every place it's saved.</summary>
public sealed record ItemAuditProblem(ItemAuditKind Kind, long Uid, int ItemId, IReadOnlyList<string> Places)
{
    public override string ToString() => $"{Kind}: serial {Uid} (item {ItemId}) at {string.Join("; ", Places)}";
}
