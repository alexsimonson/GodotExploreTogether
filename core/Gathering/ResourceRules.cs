using ExploreTogether.Core.Items;

namespace ExploreTogether.Core.Gathering;

/// <summary>
/// A kind of gatherable resource (port of the Resource component's settings on a prefab).
/// Kind is the stable id used on the network and in logs.
/// </summary>
public sealed record ResourceDef(
    string Kind,
    string Name,
    ToolRole RequiredRole,
    IReadOnlyList<int> Drops,   // the original's resources[]: one of these per successful harvest
    bool SingleUse,             // depletes on every success; otherwise only by chance (see ResourceRules)
    double RespawnSeconds = ResourceRules.DefaultRespawnSeconds,
    string Verb = "Gather");    // shown in the prompt: "Chop Tree"

/// <summary>The resources in the game. The original had one: TreeResource.prefab.</summary>
public static class ResourceCatalog
{
    /// <summary>TreeResource.prefab: name "Tree", needs an Axe, gives a Log, not single use.</summary>
    public static readonly ResourceDef Tree = new("tree", "Tree", ToolRole.Axe, [ItemCatalog.LogId], SingleUse: false, Verb: "Chop");

    public static readonly IReadOnlyList<ResourceDef> All = [Tree];

    public static ResourceDef? Get(string kind) => All.FirstOrDefault(r => r.Kind == kind);
}

public enum HarvestOutcome
{
    /// <summary>Nothing left until it respawns (the original returned silently).</summary>
    Depleted,
    /// <summary>No tool equipped, or the wrong kind ("Incorrect tool for resource.").</summary>
    WrongTool,
    /// <summary>The roll failed ("No resource for you").</summary>
    NothingFound,
    /// <summary>The roll succeeded: the player gets ItemId.</summary>
    Gathered,
}

/// <param name="ItemId">What was gathered (Gathered only).</param>
/// <param name="DepletesNow">The resource runs out after this harvest and starts its respawn timer.</param>
public readonly record struct HarvestResult(HarvestOutcome Outcome, int? ItemId = null, bool DepletesNow = false);

/// <summary>
/// Port of Resource.Interaction, ChanceDepletion and GiveResource ("osrs type solution for rng"):
/// - The equipped weapon must be a Tool of the resource's role.
/// - Roll 0..511: success on 12..399 (388 of 512, about 76%).
/// - On success the player gets one of the resource's drops. A single-use resource is then depleted;
///   otherwise it depletes on a second roll of 100..200 (101 of 512, about 20%).
/// - A depleted resource comes back after its respawn time (5 s).
/// Deliberate difference: GiveResource used Random.Range(0, Length - 1), which can never pick the last
/// drop when there are several; here every drop can come up. (With the tree's one drop it's the same.)
/// </summary>
public static class ResourceRules
{
    public const int RngSize = 512;
    public const int MinBounds = 12;
    public const int MaxBounds = 399;
    public const int DepleteMin = 100;
    public const int DepleteMax = 200;
    public const double DefaultRespawnSeconds = 5;

    /// <summary>
    /// Not in the original (which rolled on every key press): the shortest time between two harvests by one
    /// player, so holding or spamming E isn't faster than chopping with the axe (whose swing has its own cooldown).
    /// </summary>
    public const double GatherCooldown = 0.6;

    public static bool HasRightTool(ResourceDef resource, ItemDef? weapon) =>
        weapon is { Class: ItemClass.Tool } && weapon.Role == resource.RequiredRole;

    public static HarvestResult Harvest(ResourceDef resource, bool depleted, ItemDef? weapon, Random random)
    {
        if (depleted) return new(HarvestOutcome.Depleted);
        if (!HasRightTool(resource, weapon)) return new(HarvestOutcome.WrongTool);
        if (!InRange(random.Next(0, RngSize), MinBounds, MaxBounds)) return new(HarvestOutcome.NothingFound);

        var itemId = resource.Drops[random.Next(0, resource.Drops.Count)];
        var depletes = resource.SingleUse || InRange(random.Next(0, RngSize), DepleteMin, DepleteMax);
        return new(HarvestOutcome.Gathered, itemId, depletes);
    }

    private static bool InRange(int roll, int min, int max) => roll >= min && roll <= max;
}
