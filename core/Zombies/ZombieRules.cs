using ExploreTogether.Core.Items;

namespace ExploreTogether.Core.Zombies;

/// <summary>What a hit does to a zombie: damage, how long it's knocked down, whether it's destroyed outright.</summary>
public readonly record struct ZombieHit(int Damage, float KnockdownSeconds, bool Destroyed);

/// <summary>
/// How zombies take punishment, by weapon (each weapon has its use):
/// - Guns: a headshot destroys one outright; body shots barely slow it (pistol rounds take many; slugs a few).
/// - Blunt weapons (bat, concrete block, chairs): a solid blow knocks it down for a few seconds. Good for escaping.
/// - Blades and axes: heavy damage; the best way to finish one up close.
/// - Magic: the area's damage as usual; frost freezes (the layer applies it).
/// - Noise and light draw them: gunfire carries furthest, and a lit flashlight at night is seen from afar.
/// </summary>
public static class ZombieRules
{
    public const int MaxHealth = 60;
    /// <summary>A hit this high above the feet (m) is a headshot.</summary>
    public const float HeadHeight = 1.45f;
    public const float KnockdownSeconds = 3.5f;
    public const int BiteDamage = 12;
    public const float BiteRange = 1.3f, BiteSeconds = 1.4f;
    /// <summary>Walking speeds (m/s): shambling about, and after someone.</summary>
    public const float WanderSpeed = 0.6f, ChaseSpeed = 1.6f;
    /// <summary>How far they see someone: by day, at night, and someone with a lit flashlight at night.</summary>
    public const float SightDay = 16, SightNight = 8, SightFlashlight = 30;
    /// <summary>Noises carry this much further to a zombie than to a person (they're drawn to sound).</summary>
    public const float HearingFactor = 1.5f;
    /// <summary>Someone killed by a zombie gets up as one after this long (seconds).</summary>
    public const float RiseSeconds = 10;

    /// <param name="weapon">What hit it (null: an area of magic, already rolled as <paramref name="baseDamage"/>).</param>
    /// <param name="head">The hit landed on the head.</param>
    public static ZombieHit Hit(ItemDef? weapon, int baseDamage, bool head)
    {
        switch (weapon)
        {
            case { Class: ItemClass.Gun } gun:
                if (head) return new(MaxHealth, 0, true);
                return new(gun.Breaches ? baseDamage * 2 : baseDamage / 2, 0, false);
            case { KnocksOut: true }:
                return new(baseDamage, KnockdownSeconds, false);
            case { Class: ItemClass.Melee or ItemClass.Tool }:
                return new((int)(baseDamage * 2.5f), 0, false);
            default:
                return new(baseDamage, 0, false);
        }
    }

    /// <summary>How far a zombie sees someone, by daylight (0..1) and whether they carry a lit flashlight.</summary>
    public static float SightRange(float daylight, bool flashlight) =>
        flashlight && daylight < 0.5f ? SightFlashlight : SightNight + (SightDay - SightNight) * daylight;
}
