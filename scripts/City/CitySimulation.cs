using System;
using System.Collections.Generic;
using System.Linq;
using ExploreTogether.Core.City;
using Godot;

namespace ExploreTogether.City;

/// <summary>
/// Server-only, one per city instance: runs the city's clock and every resident's <see cref="CityNpcBrain"/>,
/// and writes their positions into the replicated <see cref="CityNpc"/> nodes. Brains update at 20 Hz.
/// </summary>
public partial class CitySimulation : Node
{
    private const double TickSeconds = 0.05;

    public CityClock Clock { get; private set; } = null!;
    public CityPopulation Population { get; private set; } = null!;
    private readonly List<(CityNpcBrain Brain, CityNpc Node)> _npcs = new();
    private double _accumulator;
    private double _logTimer, _clockBroadcastTimer;
    private string _label = "";

    /// <summary>Called every few seconds with the clock (hour of day, game hours per second), for clients.</summary>
    public event Action<float, float>? ClockBroadcast;

    private static readonly bool DevLog = CmdArgs.Has("dev-log-npcs");

    public void Init(string label, CityClock clock, CityPopulation population) =>
        (_label, Clock, Population) = (label, clock, population);

    public void Add(CityNpcBrain brain, CityNpc node)
    {
        _npcs.Add((brain, node));
        Apply(brain, node);
    }

    public override void _PhysicsProcess(double delta)
    {
        _accumulator += delta;
        while (_accumulator >= TickSeconds)
        {
            _accumulator -= TickSeconds;
            Clock.Advance(TickSeconds);
            foreach (var (brain, node) in _npcs)
            {
                brain.Update((float)TickSeconds, Clock.Time);
                Apply(brain, node);
            }
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
        var position = new Vector3(brain.X, 0, brain.Z);
        node.NetPosition = position;
        node.Position = position;
        node.NetYaw = brain.Yaw;
        node.Rotation = new Vector3(0, brain.Yaw, 0);
        node.NetMoving = brain.Moving;
        if (node.NetActivity != brain.Activity) node.NetActivity = brain.Activity;
    }
}
