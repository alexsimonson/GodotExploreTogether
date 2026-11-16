using ExploreTogether.Core.Items;

namespace ExploreTogether.Core.Combat;

/// <summary>How an attack with a weapon works (port of Weapon.Stab/Shoot, Tool.Attack, Magic + AreaOfEffect).</summary>
public sealed record WeaponProfile(
    ItemClass Class,
    float Range,          // how far the attack reaches (m)
    int Damage,           // fixed damage (melee/gun); for magic the maximum of a random roll
    bool Knockback,       // melee/tool knock the target back
    float Cooldown);      // seconds between attacks (not in the original; see CombatRules)

/// <summary>
/// The original combat numbers, ported:
/// - Melee (Weapon.Stab): ray 4 m from the camera, 20 damage + knockback. (Note: Stab ignored the
///   weapon's own damage field and always dealt 20; kept.)
/// - Tool (Tool.Attack): same as melee: 4 m, 20 damage + knockback.
/// - Gun (Weapon.Shoot): ray with unlimited range, 100 damage; uses ammo (GunAmmo).
/// - Magic (Magic.Attack + PlayerGhostAim + AreaOfEffect): place an area within 10 m where you aim;
///   enemies entering it take Random.Range(0, max_damage) damage once; the area lasts 5 s.
///   Frost freezes them for `timer` (5 s); Blood heals the caster 10 per enemy hit.
/// Difference: attacks have a short cooldown. The original had none (one attack per click), which in
/// multiplayer would let an auto-clicker attack every frame.
/// </summary>
public static class CombatRules
{
    public const float MeleeRange = 4f;
    public const int MeleeDamage = 20;
    public const int GunDamage = 100;
    public const float GunRange = 1000f;
    public const float MagicPlaceRange = 10f;
    public const float MagicAreaRadius = 1.5f;
    public const float MagicAreaLifetime = 5f;
    public const float FrostFreezeSeconds = 5f;
    public const int BloodHealPerHit = 10;
    public const int MagicMaxDamage = 50;

    public static WeaponProfile? ProfileFor(ItemDef? weapon)
    {
        WeaponProfile? profile = weapon?.Class switch
        {
            ItemClass.Melee => new(ItemClass.Melee, MeleeRange, MeleeDamage, true, 0.5f),
            ItemClass.Tool => new(ItemClass.Tool, MeleeRange, MeleeDamage, true, 0.5f),
            ItemClass.Gun => new(ItemClass.Gun, GunRange, GunDamage, false, 0.25f),
            ItemClass.Magic => new(ItemClass.Magic, MagicPlaceRange, MagicMaxDamage, false, 0.75f),
            _ => null,
        };
        if (profile == null) return null;
        return profile with { Range = weapon!.Range ?? profile.Range, Cooldown = weapon.Cooldown ?? profile.Cooldown };
    }

    /// <summary>How close a shotgun must be to a deadbolt for the slug to destroy it (m, from the head).</summary>
    public const float BreachRange = 6f;

    /// <summary>Port of Magic.Attack's damage roll: Random.Range(0, max_damage), i.e. 0..max-1.</summary>
    public static int RollMagicDamage(Random random, int maxDamage = MagicMaxDamage) => random.Next(0, maxDamage);

    /// <summary>Blood Wand vs Ice Wand: the original Magic.MagicStyle (asset values Blood = 0, Frost = 1).</summary>
    public static MagicStyle MagicStyleOf(ItemDef weapon) => weapon.Id switch
    {
        567 => MagicStyle.Frost, // Ice Wand
        _ => MagicStyle.Blood,   // Blood Wand (and the class default)
    };
}

public enum MagicStyle { Blood, Frost, Fire, Earth }

/// <summary>Port of the Health component: clamp at 0 and max, report death once.</summary>
public sealed class Health(int max = 100)
{
    public int Max { get; } = max;
    public int Current { get; private set; } = max;
    public bool IsDead => Current <= 0;

    /// <summary>Returns true if this damage killed (it was alive before, dead now).</summary>
    public bool Damage(int amount)
    {
        if (IsDead || amount <= 0) return false;
        Current = Math.Max(0, Current - amount);
        return IsDead;
    }

    /// <summary>Port of AddHealthAmount: heal, capped at max. Dead stays dead.</summary>
    public void Heal(int amount)
    {
        if (IsDead || amount <= 0) return;
        Current = Math.Min(Max, Current + amount);
    }

    public void Reset() => Current = Max;
}

/// <summary>
/// Port of Gun.Attack / Gun.Reload with the ammo moved where it belongs:
/// - the rounds LOADED are part of that particular gun (ItemSlot.data), so they go with it when it changes
///   hands, is dropped or saved (the original kept them on the shared item asset, so every pistol shared one);
/// - the reserve is ammo ITEMS in the inventory (the original's bulletCount).
/// Firing needs a loaded round; reloading tops the gun up from the ammo items, same arithmetic as the original.
/// </summary>
public static class GunRules
{
    public static int Loaded(ItemSlot weapon) => weapon.data ?? 0;

    public static int Reserve(Inventory inventory, ItemDef gun) =>
        gun.AmmoItemId is { } ammo ? inventory.CountOf(ammo) : 0;

    public static bool TryFire(ItemSlot weapon)
    {
        if ((weapon.data ?? 0) <= 0) return false; // "Out of ammo"
        weapon.data -= 1;
        return true;
    }

    /// <summary>Moves rounds from the inventory into the gun. Returns how many were loaded (0 = nothing to do).</summary>
    public static int Reload(ItemSlot weapon, ItemDef gun, Inventory inventory)
    {
        if (gun.MagazineSize is not { } size || gun.AmmoItemId is not { } ammoId) return 0;
        var needed = size - Loaded(weapon);
        var loaded = 0;
        for (int i = 0; i < inventory.slots.Length && loaded < needed; i++)
        {
            var slot = inventory.slots[i];
            if (slot.itemId != ammoId) continue;
            var take = Math.Min(slot.stack_size, needed - loaded);
            slot.stack_size -= take;
            loaded += take;
            if (slot.stack_size <= 0) inventory.RemoveItem(i);
        }
        weapon.data = Loaded(weapon) + loaded;
        return loaded;
    }
}
