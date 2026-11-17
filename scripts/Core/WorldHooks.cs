using Godot;

namespace ExploreTogether;

/// <summary>
/// Something weapons can hit that the game doesn't know by name (players, dungeon enemies and city residents are
/// handled directly). Layers that add new kinds of creature (see Zombies) implement it on the node that owns the
/// hitbox, so combat code never has to change for them.
/// </summary>
public interface IHittable
{
    /// <summary>Can it be hit right now (not already destroyed)?</summary>
    bool CanBeHit { get; }

    Vector3 GlobalPosition { get; }

    /// <summary>A gun, blade or club hit landing at <paramref name="at"/>, from <paramref name="from"/> (the attacker's head).</summary>
    void TakeWeaponHit(Core.Items.ItemDef weapon, int damage, Vector3 at, Vector3 from, long attacker);

    /// <summary>Caught in a magic area: its damage, and whether it's frost (freezes).</summary>
    void TakeAreaHit(int damage, bool frost, Vector3 center, long attacker);
}

/// <summary>A body players keep out of (as they do residents and enemies), for creatures added by layers.</summary>
public interface ISolidBody
{
    bool Solid { get; }
    float Radius { get; }
}
