using ExploreTogether.City;
using ExploreTogether.Core.Audio;
using ExploreTogether.Core.City;
using Godot;
using Noise = ExploreTogether.Core.City.Noise;

namespace ExploreTogether.Server;

/// <summary>
/// A city's power and lights (see Core.City.CityPower, City.CityPowerProps): E on a fuse box, light switch, substation
/// or the main breaker; and lights and fuse boxes broken by a shot or a blow (from <see cref="HitScan"/>). All of it
/// is loud enough to be noticed by anyone nearby, and none of it is anyone's business but the owner's.
/// </summary>
public partial class ServerGame
{
    /// <summary>Handles a power interaction; false if the id isn't one.</summary>
    private bool UsePower(PeerSession s, ServerInstance city, string interactionId)
    {
        if (city.Power is not { } power || city.City is not { } layout) return false;
        var name = s.Character!.Name;
        var at = _instances.FindPlayer(s.Peer)?.Player.GlobalPosition ?? Vector3.Zero;
        if (Parse(interactionId, CityPowerProps.FuseBoxPrefix) is { } parcel && parcel < layout.Parcels.Count)
        {
            if (power.BoxState(parcel) == CityPower.Box.Locked)
            {
                _session.SendNotice(s.Peer, "The fuse box is locked. Break it open first (hit it, or shoot the lock).");
                return true;
            }
            power.FlipBreaker(parcel);
            var cut = power.BoxState(parcel) == CityPower.Box.Cut;
            s.LastBreakingAt = Now;
            EmitNoise(city, new Noise(at.X, at.Z, 6, "breaker", Severity.Suspicious, s.Peer, at.Y));
            PlaySound(city, SoundId.Breaker, at + Vector3.Up * 1.4f);
            _session.SendNotice(s.Peer, cut ? "You cut the building's power." : "You restore the building's power.");
            GD.Print($"[server] '{name}' {(cut ? "cuts" : "restores")} the power of building {parcel} in city {city.Id}");
            return true;
        }
        if (Parse(interactionId, CityPowerProps.SwitchPrefix) is { } room && room < layout.Parcels.Count)
        {
            var lit = city.Doors![room].Lit;
            power.FlipSwitch(room, lit);
            PlaySound(city, SoundId.LightSwitch, at + Vector3.Up * 1.3f);
            if (!power.CanLight(room)) _session.SendNotice(s.Peer, "Click. Nothing: no power.");
            GD.Print($"[server] '{name}' switches the lights of building {room} {(lit ? "off" : "on")} in city {city.Id}");
            return true;
        }
        if (Parse(interactionId, CityPowerProps.SubstationPrefix) is { } district && district < power.Districts)
        {
            var down = !power.SubstationDown(district);
            power.SetSubstation(district, down);
            s.LastBreakingAt = Now;
            EmitNoise(city, new Noise(at.X, at.Z, 30, "sparks", Severity.Crime, s.Peer, at.Y));
            PlaySound(city, SoundId.Sparks, at + Vector3.Up);
            _session.SendNotice(s.Peer, down ? "Sparks fly: the district goes dark." : "The district's power hums back on.");
            GD.Print($"[server] '{name}' {(down ? "cuts" : "restores")} the power of district {district} in city {city.Id}");
            return true;
        }
        if (interactionId == CityPowerProps.BreakerId)
        {
            var down = !power.StationDown;
            power.SetStation(down);
            s.LastBreakingAt = Now;
            EmitNoise(city, new Noise(at.X, at.Z, 20, "breaker", Severity.Crime, s.Peer, at.Y));
            PlaySound(city, SoundId.MainBreaker, at + Vector3.Up);
            _session.SendNotice(s.Peer, down ? "With a heavy clunk, the whole city goes dark." : "The city's lights come back on.");
            GD.Print($"[server] '{name}' {(down ? "cuts" : "restores")} the power of the whole city {city.Id}");
            return true;
        }
        return false;

        static int? Parse(string id, string prefix) =>
            id.StartsWith(prefix) && int.TryParse(id[prefix.Length..], out var n) && n >= 0 ? n : null;
    }

    /// <summary>
    /// A shot or a blow landed on a light or a fuse box (see <see cref="HitScan"/>): the light breaks, the box's lock
    /// gives. Returns true if it was one of those (the hit stops there).
    /// </summary>
    private bool HitPowerProp(PeerSession s, ServerInstance city, GodotObject collider, Vector3 at)
    {
        if (city.Power is not { } power) return false;
        switch (collider)
        {
            case CityLightTarget light:
                var broke = light.Kind switch
                {
                    CityLightTarget.Kinds.Lamp => power.BreakLamp(light.Index),
                    CityLightTarget.Kinds.Porch => power.BreakPorch(light.Index),
                    _ => power.BreakCeiling(light.Index),
                };
                if (broke)
                {
                    s.LastBreakingAt = Now;
                    EmitNoise(city, new Noise(at.X, at.Z, 14, "glass breaking", Severity.Suspicious, s.Peer, at.Y - 1.6f));
                    PlaySound(city, SoundId.GlassBreak, at);
                    GD.Print($"[server] '{s.Character!.Name}' breaks {light.Kind.ToString().ToLowerInvariant()} light {light.Index} in city {city.Id}");
                }
                return true;
            case CityFuseBox box:
                if (power.BreakBoxOpen(box.ParcelId))
                {
                    s.LastBreakingAt = Now;
                    EmitNoise(city, new Noise(at.X, at.Z, 10, "metal bang", Severity.Suspicious, s.Peer, at.Y - 1.4f));
                    PlaySound(city, SoundId.FuseBoxBang, at);
                    _session.SendNotice(s.Peer, "The fuse box's lock gives.");
                    GD.Print($"[server] '{s.Character!.Name}' breaks open the fuse box of building {box.ParcelId} in city {city.Id}");
                }
                return true;
        }
        return false;
    }
}
