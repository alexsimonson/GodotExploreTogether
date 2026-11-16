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
/// --city-loops P (extra streets in percent, default 15).
/// </summary>
public partial class ServerGame
{
    private static readonly int CitySize = CmdArgs.GetInt("city-size", 5);
    private static readonly int CityLoops = CmdArgs.GetInt("city-loops", 15);
    private static readonly int? CitySeed = CmdArgs.Has("city-seed") ? CmdArgs.GetInt("city-seed", 0) : null;

    private void UseCityGate(PeerSession s, ServerInstance instance)
    {
        if (instance == _instances.Hub)
        {
            // A city counts as in use while someone is in it or still loading into it.
            var city = _instances.Cities.FirstOrDefault(c =>
                           c.Players.Count > 0 || _sessions.Values.Any(o => o.PendingInstanceId == c.Id))
                       ?? _instances.CreateCity(CitySize, CitySize, CitySeed, CityLoops, s.Character!.Name);
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
