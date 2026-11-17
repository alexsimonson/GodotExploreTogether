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

    /// <summary>Five times a second (with the seconds since the last): the server works out what everyone perceives.</summary>
    public event Action<float>? Perception;

    /// <summary>After every step: the server carries out what the residents decided to do.</summary>
    public event Action? Stepped;

    public const double PerceptionSeconds = 0.2;
    private double _perceptionTimer, _lightsTimer;
    private CityLayout? _city;
    private CityDoors? _doors;

    /// <summary>The city's electricity and light switches (null: lights only follow the residents).</summary>
    public CityPower? Power { get; set; }
    /// <summary>Per building: how long someone who belongs there has been awake inside with the lights switched off.</summary>
    private float[] _darkFor = [];
    /// <summary>A switched-off light gets noticed (and switched back on) after this long (real seconds).</summary>
    public const float NoticeDarkSeconds = 12;

    private static readonly bool DevLog = CmdArgs.Has("dev-log-npcs");

    /// <param name="players">Where the players in this city are now.</param>
    public void Init(string label, CityClock clock, CityPopulation population, Func<IEnumerable<Vector3>> players) =>
        (_label, Clock, Population, _players) = (label, clock, population, players);

    /// <summary>Gives the residents the city's doors (after everyone is added: doors start locked where nobody is inside).</summary>
    public void SetUpDoors(CityLayout city, CityDoors doors)
    {
        (_city, _doors) = (city, doors);
        _crowd.SetUpDoors(city, doors, Clock.Time);
        UpdateLights();
    }

    /// <summary>Every building with someone awake inside is lit after dark (clients light its windows; it helps residents see).</summary>
    /// <param name="seconds">Real seconds since the last update (for noticing switched-off lights).</param>
    private void UpdateLights(float seconds = 0)
    {
        if (_city == null || _doors == null) return;
        var dark = CityLighting.Daylight(Clock.HourOfDay) < 0.5f;
        var want = new bool[_city.Parcels.Count];
        var homeAwake = new CityNpcBrain?[_city.Parcels.Count]; // someone who belongs there, awake inside
        if (dark)
        {
            foreach (var (brain, _) in _npcs)
                if (!brain.IsDown && brain.Activity != "sleeping" && _city.ParcelAt(brain.X, brain.Z) is { } p && p.Footprint.Contains(brain.X, brain.Z))
                {
                    want[p.Id] = true;
                    if (brain.Person.Home == p.Id || brain.Person.Work == p.Id) homeAwake[p.Id] = brain;
                }
            // Police stations are staffed all night (always lit); so are taverns while open, and the power station.
            foreach (var p in _city.Parcels)
                if (p.Building is BuildingType.Guardhouse or BuildingType.PowerStation
                    || p.Building == BuildingType.Tavern && Stealth.IsOpen(p.Building, Clock.HourOfDay))
                    want[p.Id] = true;
        }
        if (_darkFor.Length != want.Length) _darkFor = new float[want.Length];
        for (var i = 0; i < want.Length; i++)
        {
            // Someone at home in the dark (a player switched it off): after a while they notice, and switch it back on.
            if (Power?.SwitchState(i) == LightSwitch.Off && homeAwake[i] is { } who)
            {
                if ((_darkFor[i] += seconds) >= NoticeDarkSeconds)
                {
                    _darkFor[i] = 0;
                    Power.ResetSwitch(i);
                    who.Remark("Who turned the lights off?");
                    GD.Print($"[server] {_label}: {who.Person.Name} switches the lights of building {i} back on");
                }
            }
            else _darkFor[i] = 0;
            _doors.SetLit(i, dark && (Power?.BuildingLit(i, want[i]) ?? want[i]));
        }
    }

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
            _crowd.SetPlayers(_players().Select(p => (p.X, p.Z, p.Y)));
            _crowd.Step((float)TickSeconds, Clock.Time);
            if ((_perceptionTimer += TickSeconds) >= PerceptionSeconds)
            {
                Perception?.Invoke((float)_perceptionTimer);
                _perceptionTimer = 0;
            }
            Stepped?.Invoke();
            foreach (var (brain, node) in _npcs) Apply(brain, node);
        }
        if ((_lightsTimer += delta) >= 1)
        {
            UpdateLights((float)_lightsTimer);
            _lightsTimer = 0;
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
        if (node.NetMood != (int)brain.Mood) node.NetMood = (int)brain.Mood;
        if (brain.SpeechAt > node.SpokeAt)
        {
            node.SpokeAt = brain.SpeechAt;
            node.NetSpeech = $"{(int)(brain.SpeechAt * 10) % 10000}|{brain.Speech}";
        }
        var weapon = brain.WeaponOut ? node.Gear.EquippedWeapon?.Id ?? -1 : -1;
        if (node.NetWeapon != weapon) node.NetWeapon = weapon;
        if (node.NetHeld != brain.Held) node.NetHeld = brain.Held;
        if (node.NetFrozen != brain.Frozen) node.NetFrozen = brain.Frozen;
        var position = new Vector3(brain.X, brain.Y, brain.Z);
        node.NetPosition = position;
        node.Position = position;
        node.NetYaw = brain.Yaw;
        node.Rotation = new Vector3(0, brain.Yaw, 0);
        node.NetMoving = brain.Moving;
        if (node.NetActivity != brain.Activity) node.NetActivity = brain.Activity;
    }
}
