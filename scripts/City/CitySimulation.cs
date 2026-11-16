using System;
using System.Collections.Generic;
using System.Linq;
using ExploreTogether.Core.City;
using Godot;

namespace ExploreTogether.City;

/// <summary>
/// Server-only, one per city instance: runs the city's clock and every resident's <see cref="CityNpcBrain"/>,
/// and writes their positions into the replicated <see cref="CityNpc"/> nodes. Brains update at 20 Hz, as one
/// <see cref="CityCrowd"/>, so residents keep out of the players (at their latest positions) and of each other.
/// </summary>
public partial class CitySimulation : Node
{
    private const double TickSeconds = 0.05;

    public CityClock Clock { get; private set; } = null!;
    public CityPopulation Population { get; private set; } = null!;
    private readonly List<(CityNpcBrain Brain, CityNpc Node)> _npcs = new();
    private readonly CityCrowd _crowd = new();
    private Func<IEnumerable<Vector3>> _players = () => [];
    private double _accumulator;
    private double _logTimer, _clockBroadcastTimer;
    private string _label = "";

    /// <summary>Called every few seconds with the clock (hour of day, game hours per second), for clients.</summary>
    public event Action<float, float>? ClockBroadcast;

    private static readonly bool DevLog = CmdArgs.Has("dev-log-npcs");

    /// <param name="players">Where the players in this city are now.</param>
    public void Init(string label, CityClock clock, CityPopulation population, Func<IEnumerable<Vector3>> players) =>
        (_label, Clock, Population, _players) = (label, clock, population, players);

    /// <summary>Gives the residents the city's doors (after everyone is added: doors start locked where nobody is inside).</summary>
    public void SetUpDoors(CityLayout city, CityDoors doors) => _crowd.SetUpDoors(city, doors, Clock.Time);

    public void Add(CityNpcBrain brain, CityNpc node)
    {
        _npcs.Add((brain, node));
        _crowd.Add(brain);
        Apply(brain, node);
    }

    public override void _PhysicsProcess(double delta)
    {
        _accumulator += delta;
        while (_accumulator >= TickSeconds)
        {
            _accumulator -= TickSeconds;
            Clock.Advance(TickSeconds);
            _crowd.SetPlayers(_players().Select(p => (p.X, p.Z)));
            _crowd.Step((float)TickSeconds, Clock.Time);
            foreach (var (brain, node) in _npcs) Apply(brain, node);
        }

        if ((_clockBroadcastTimer += delta) >= 5)
        {
            _clockBroadcastTimer = 0;
            ClockBroadcast?.Invoke(Clock.HourOfDay, Clock.HoursPerSecond);
        }
        if (DevLog && (_logTimer += delta) >= 5)
        {
            _logTimer = 0;
            GD.Print($"[server] {_label} at {CityClock.Format(Clock.HourOfDay)}: {Summary()}");
        }
    }

    /// <summary>"12 sleeping, 9 working, 6 walking, ...": what everyone is doing.</summary>
    public string Summary() => string.Join(", ", _npcs.GroupBy(n => n.Brain.Activity)
        .OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"));

    private static void Apply(CityNpcBrain brain, CityNpc node)
    {
        if (node.IsDown) return;
        var position = new Vector3(brain.X, 0, brain.Z);
        node.NetPosition = position;
        node.Position = position;
        node.NetYaw = brain.Yaw;
        node.Rotation = new Vector3(0, brain.Yaw, 0);
        node.NetMoving = brain.Moving;
        if (node.NetActivity != brain.Activity) node.NetActivity = brain.Activity;
    }
}
