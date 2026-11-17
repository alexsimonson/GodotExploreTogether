namespace ExploreTogether.Core.Audio;

/// <summary>
/// Every one-shot sound the server can tell clients to play (replicated as ints: append only, never reorder).
/// Clients play their own footsteps, menus and ambience without the server (see the client's GameAudio).
/// </summary>
public enum SoundId
{
    None,
    PistolShot,
    ShotgunShot,
    Reload,
    ShotgunReload,
    DryFire,
    Swing,
    HitFlesh,
    HitBlunt,
    Chop,
    MagicIce,
    MagicBlood,
    EnemyAttack,
    EnemyHurt,
    EnemyDeath,
    PlayerHurt,
    PlayerDeath,
    NpcHurt,
    NpcDeath,
    Scream,
    DoorOpen,
    DoorClose,
    DoorLock,
    DoorKick,
    DoorBreak,
    LockPick,
    LockpickBreak,
    Grab,
    BreakFree,
    ItemPickup,
    ItemDrop,
    ExitOpen,
    Travel,
    FurnitureBreak,
    FurnitureGrab,
    FlashlightClick,
    GlassBreak,
    LightSwitch,
    FuseBoxBang,
    Breaker,
    Sparks,
    MainBreaker,
}

/// <summary>How far each sound carries (m): the server only sends it to players that close.</summary>
public static class Sounds
{
    public static float Range(SoundId id) => id switch
    {
        SoundId.PistolShot => 120,
        SoundId.ShotgunShot => 140,
        SoundId.DoorKick or SoundId.DoorBreak => 60,
        SoundId.Scream => 50,
        SoundId.ExitOpen => 80,
        SoundId.EnemyAttack or SoundId.EnemyHurt or SoundId.EnemyDeath => 35,
        SoundId.LockPick => 10,
        SoundId.ItemPickup or SoundId.ItemDrop or SoundId.DryFire => 12,
        _ => 30,
    };
}
