using System.Linq;
using ExploreTogether.Net;
using Godot;

namespace ExploreTogether.Server;

/// <summary>
/// City prototype: the City Gate in the hub takes a player to the city, and the gate in the city brings them back.
///
/// One city at a time per group: the gate joins a city that has players in it (or arriving), so friends end up together.
/// When nobody is in one, it generates a new city (a new seed each visit). Empty cities close after the same
/// timeout as dungeons.
///
/// Server args: --city-size N (tiles per side, default 5), --city-seed S (fixed seed, default random),
/// --city-loops P (extra streets in percent, default 15), --city-favor NAME (dev: place that building design wherever it fits),
/// --city-people N (residents, default about 1.6 per tile), --city-start-hour H (default 8), --city-day-minutes M
/// (real minutes per game day, default 48), --dev-log-npcs (log what the residents are doing every 5 s).
/// </summary>
public partial class ServerGame
{
    private static readonly int CitySize = CmdArgs.GetInt("city-size", 5);
    private static readonly int CityLoops = CmdArgs.GetInt("city-loops", 15);
    private static readonly int? CitySeed = CmdArgs.Has("city-seed") ? CmdArgs.GetInt("city-seed", 0) : null;
    private static readonly string? CityFavor = CmdArgs.Has("city-favor") ? CmdArgs.Get("city-favor") : null;
    private static readonly int? CityPeople = CmdArgs.Has("city-people") ? CmdArgs.GetInt("city-people", 0) : null;
    private static readonly float CityStartHour = CmdArgs.GetFloat("city-start-hour", 8);
    private static readonly float CityDayMinutes = CmdArgs.GetFloat("city-day-minutes", 48);

    /// <summary>A player has arrived in a city: tell them the time.</summary>
    private void OnEnteredCity(PeerSession s, ServerInstance city)
    {
        if (city.Simulation is { } sim)
            _session.SendCityClock(s.Peer, sim.Clock.HourOfDay, sim.Clock.HoursPerSecond);
    }

    private ServerInstance CreateCity(string createdBy)
    {
        var city = _instances.CreateCity(CitySize, CitySize, CitySeed, CityLoops, createdBy, CityFavor,
            CityPeople, CityStartHour, CityDayMinutes);
        // Keep everyone's clock in step (clients run it on between updates).
        city.Simulation!.ClockBroadcast += (hour, rate) =>
        {
            foreach (var peer in city.Players.Keys) _session.SendCityClock(peer, hour, rate);
        };
        return city;
    }

    private void UseCityGate(PeerSession s, ServerInstance instance)
    {
        if (instance == _instances.Hub)
        {
            // A city counts as in use while someone is in it or still loading into it.
            var city = _instances.Cities.FirstOrDefault(c =>
                           c.Players.Count > 0 || _sessions.Values.Any(o => o.PendingInstanceId == c.Id))
                       ?? CreateCity(s.Character!.Name);
            Save(s, CityReturnPoint); // a later login starts in front of the gate, not inside it
            GD.Print($"[server] '{s.Character!.Name}' goes to city {city.Id}");
            SendToInstance(s, city);
        }
        else if (instance.Kind == InstanceKinds.City)
        {
            GD.Print($"[server] '{s.Character!.Name}' returns to the hub from city {instance.Id}");
            SendToInstance(s, _instances.Hub);
        }
    }
}
