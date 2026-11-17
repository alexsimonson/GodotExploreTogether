using System.Runtime.CompilerServices;
using ExploreTogether.Core.Audio;
using ExploreTogether.Core.City;
using ExploreTogether.Core.Items;
using Godot;

namespace ExploreTogether.Server;

/// <summary>
/// Sounds: the server tells the players near something that happens which sound to play there (see
/// <see cref="SoundId"/>; how far each carries: <see cref="Sounds.Range"/>). Clients play their own footsteps, menu
/// clicks and ambience.
/// </summary>
public partial class ServerGame
{
    /// <summary>Plays a sound at a point for every player in the instance within its range.</summary>
    private void PlaySound(ServerInstance instance, SoundId id, Vector3 at)
    {
        if (id == SoundId.None) return;
        var range = Sounds.Range(id);
        foreach (var (peer, player) in instance.Players)
            if (player.GlobalPosition.DistanceSquaredTo(at) <= range * range)
                _session.SendSound(peer, (int)id, at);
    }

    /// <summary>The sound of firing (or reloading) a gun: shotguns sound different from pistols.</summary>
    private static SoundId ShotSound(ItemDef gun) => gun.Breaches ? SoundId.ShotgunShot : SoundId.PistolShot;
    private static SoundId ReloadSound(ItemDef gun) => gun.Breaches ? SoundId.ShotgunReload : SoundId.Reload;

    /// <summary>City noises that have a sound of their own (gunshots are played where the gun is fired).</summary>
    private static SoundId NoiseSound(Core.City.Noise noise) => noise.Cause switch
    {
        "scream" => SoundId.Scream,
        "fight" => SoundId.HitBlunt,
        "door kicked" or "door kicked in" => SoundId.DoorKick,
        "lockpicking" => SoundId.LockPick,
        _ => SoundId.None,
    };

    /// <summary>A city's doors: what they were last time, so a change can be heard (opened, shut, locked, broken).</summary>
    private readonly ConditionalWeakTable<CityDoors, int[]> _doorFlags = new(); // forgotten with the city

    private void PlayDoorSound(ServerInstance city, int parcel)
    {
        var doors = city.Doors!;
        if (!_doorFlags.TryGetValue(doors, out var flags)) return;
        var before = CityDoors.DoorState.FromFlags(flags[parcel]);
        var now = doors[parcel];
        flags[parcel] = now.Flags;
        var sound = now.Broken && !before.Broken || now.LockBroken && !before.LockBroken ? SoundId.DoorBreak
            : now.Open && !before.Open ? SoundId.DoorOpen
            : !now.Open && before.Open ? SoundId.DoorClose
            : now.Locked != before.Locked ? SoundId.DoorLock
            : SoundId.None;
        var p = city.City!.Parcels[parcel];
        PlaySound(city, sound, new Vector3(p.Door.X, 1.2f, p.Door.Z));
    }

    /// <summary>Starts watching a city's doors (call once, when it's made).</summary>
    private void TrackDoorSounds(ServerInstance city)
    {
        var doors = city.Doors!;
        var flags = new int[doors.Count];
        var all = doors.Export();
        for (var i = 0; i < all.Length; i += 2) flags[all[i]] = all[i + 1];
        _doorFlags.AddOrUpdate(doors, flags);
    }
}
