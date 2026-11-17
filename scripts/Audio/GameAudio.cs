using System;
using System.Collections.Generic;
using System.Linq;
using ExploreTogether.Core.Audio;
using ExploreTogether.Core.City;
using Godot;

namespace ExploreTogether.Audio;

/// <summary>
/// Client sound: one-shots the server asks for (<see cref="SoundId"/>, at a point in the world), footsteps for every
/// character near the camera, ambience per place (hub, dungeon, city by day and by night), tavern music, and UI cues.
///
/// Sound files are looked up by name: <c>res://assets/audio/NAME.wav</c> (or .ogg/.mp3), with variants
/// <c>NAME_1</c>, <c>NAME_2</c>, ... picked at random. If there's none, the generated stand-in in
/// <c>res://assets/audio/placeholder/</c> is used. So replacing a placeholder is dropping a real file in
/// <c>assets/audio/</c> with the same name (see docs/26-11-16/AUDIO-REPORT.md for the list).
///
/// F1 shows every sound playing right now and what it imitates (<see cref="SoundMonitor"/>).
///
/// Buses (made here at startup): SFX, Footsteps, Ambience, Music, UI, all into Master. <c>--mute</c> mutes Master,
/// <c>--volume DB</c> sets it.
/// </summary>
public partial class GameAudio : Node
{
    public static GameAudio? Instance { get; private set; }

    public const string Sfx = "SFX", Steps = "Footsteps", Ambience = "Ambience", Music = "Music", Ui = "UI";

    private const string Folder = "res://assets/audio/";
    private const string PlaceholderFolder = "res://assets/audio/placeholder/";
    private const int MaxVoices = 40;

    private readonly Dictionary<string, AudioStream[]> _streams = new();
    private readonly List<AudioStreamPlayer3D> _voices = new();
    private readonly Random _random = new();

    /// <summary>How each server sound plays: file name, loudness (dB), pitch spread, and how far it carries (m).</summary>
    private static (string Name, float Db, float Pitch, float Distance) Spec(SoundId id) => id switch
    {
        SoundId.PistolShot => ("gunshot", 0, 0.05f, 120),
        SoundId.ShotgunShot => ("shotgun_shot", 2, 0.05f, 140),
        SoundId.Reload => ("reload", -4, 0.03f, 20),
        SoundId.ShotgunReload => ("shotgun_reload", -4, 0.03f, 20),
        SoundId.DryFire => ("dry_fire", -6, 0.05f, 10),
        SoundId.Swing => ("swing", -6, 0.12f, 20),
        SoundId.HitFlesh => ("hit_flesh", -2, 0.1f, 25),
        SoundId.HitBlunt => ("hit_blunt", -2, 0.1f, 25),
        SoundId.Chop => ("chop", -3, 0.1f, 30),
        SoundId.MagicIce => ("magic_ice", -3, 0.05f, 30),
        SoundId.MagicBlood => ("magic_blood", -3, 0.05f, 30),
        SoundId.EnemyAttack => ("enemy_attack", -3, 0.1f, 30),
        SoundId.EnemyHurt => ("enemy_hurt", -3, 0.12f, 30),
        SoundId.EnemyDeath => ("enemy_death", -1, 0.08f, 35),
        SoundId.PlayerHurt => ("player_hurt", -3, 0.1f, 25),
        SoundId.PlayerDeath => ("player_death", 0, 0.05f, 30),
        SoundId.NpcHurt => ("npc_hurt", -3, 0.15f, 25),
        SoundId.NpcDeath => ("npc_death", -1, 0.1f, 30),
        SoundId.Scream => ("scream", 0, 0.15f, 50),
        SoundId.DoorOpen => ("door_open", -6, 0.08f, 20),
        SoundId.DoorClose => ("door_close", -5, 0.08f, 20),
        SoundId.DoorLock => ("door_lock", -8, 0.08f, 12),
        SoundId.DoorKick => ("door_kick", 0, 0.08f, 60),
        SoundId.DoorBreak => ("door_break", 2, 0.05f, 60),
        SoundId.LockPick => ("lockpick", -10, 0.1f, 8),
        SoundId.LockpickBreak => ("lockpick_break", -6, 0.05f, 10),
        SoundId.Grab => ("grab", -4, 0.1f, 20),
        SoundId.BreakFree => ("break_free", -3, 0.1f, 20),
        SoundId.ItemPickup => ("item_pickup", -6, 0.08f, 12),
        SoundId.ItemDrop => ("item_drop", -6, 0.08f, 12),
        SoundId.ExitOpen => ("exit_open", 0, 0, 80),
        SoundId.Travel => ("travel", -4, 0, 20),
        SoundId.FurnitureBreak => ("furniture_break", 0, 0.08f, 40),
        SoundId.FurnitureGrab => ("furniture_grab", -6, 0.1f, 12),
        SoundId.FlashlightClick => ("flashlight_click", -8, 0.05f, 8),
        SoundId.GlassBreak => ("glass_break", -2, 0.1f, 30),
        SoundId.LightSwitch => ("light_switch", -8, 0.05f, 8),
        SoundId.FuseBoxBang => ("fusebox_bang", -2, 0.08f, 20),
        SoundId.Breaker => ("breaker", -4, 0.05f, 12),
        SoundId.Sparks => ("sparks", 0, 0.05f, 40),
        SoundId.MainBreaker => ("main_breaker", 2, 0, 50),
        _ => ("", 0, 0, 0),
    };

    public override void _Ready()
    {
        Instance = this;
        ProcessMode = ProcessModeEnum.Always;
        foreach (var bus in new[] { Sfx, Steps, Ambience, Music, Ui }) MakeBus(bus);
        AudioServer.SetBusVolumeDb(AudioServer.GetBusIndex(Steps), -4);
        AudioServer.SetBusVolumeDb(AudioServer.GetBusIndex(Ambience), -8);
        AudioServer.SetBusVolumeDb(AudioServer.GetBusIndex(Music), -6);
        if (CmdArgs.Has("volume")) AudioServer.SetBusVolumeDb(0, CmdArgs.GetFloat("volume", 0));
        if (CmdArgs.Has("mute")) AudioServer.SetBusMute(0, true);

        _ambienceA = MakeLoopPlayer("AmbienceA");
        _ambienceB = MakeLoopPlayer("AmbienceB");
        if (CmdArgs.Has("dev-sound-monitor")) ToggleMonitor(); // dev: open the F1 sound monitor from the start
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        // Let go of every stream, so nothing is still held when the engine shuts down.
        foreach (var v in _voices) v.Stream = null;
        _ambienceA.Stream = _ambienceB.Stream = null;
        _streams.Clear();
        _tracked.Clear();
        _voices.Clear();
        _walkers.Clear();
        _venues.Clear();
    }

    private static void MakeBus(string name)
    {
        if (AudioServer.GetBusIndex(name) >= 0) return;
        AudioServer.AddBus();
        var index = AudioServer.BusCount - 1;
        AudioServer.SetBusName(index, name);
        AudioServer.SetBusSend(index, "Master");
    }

    // ------------------------------------------------------------------ files

    /// <summary>The sound's files: the real ones in assets/audio, or else its placeholder(s). Empty if neither exists.</summary>
    private AudioStream[] Streams(string name)
    {
        if (_streams.TryGetValue(name, out var found)) return found;
        var list = Find(Folder, name);
        _sources[name] = "real";
        // A close real sound beats a generated one (the rifle shot for shotguns, until there's a shotgun blast).
        if (list.Count == 0 && StandIns.TryGetValue(name, out var standIn))
        {
            list = Find(Folder, standIn);
            _sources[name] = $"stand-in: {standIn}";
        }
        if (list.Count == 0)
        {
            list = Find(PlaceholderFolder, name);
            _sources[name] = "placeholder";
        }
        if (list.Count == 0) GD.PushWarning($"[audio] no sound file for '{name}'");
        return _streams[name] = list.ToArray();
    }

    private static List<AudioStream> Find(string folder, string name)
    {
        var found = new List<AudioStream>();
        foreach (var candidate in new[] { name }.Concat(Enumerable.Range(1, 8).Select(i => $"{name}_{i}")))
            foreach (var ext in new[] { ".wav", ".ogg", ".mp3" })
                if (ResourceLoader.Exists(folder + candidate + ext))
                {
                    found.Add(GD.Load<AudioStream>(folder + candidate + ext));
                    break;
                }
        return found;
    }

    /// <summary>Real sounds used for others until those have their own files.</summary>
    private static readonly Dictionary<string, string> StandIns = new()
    {
        ["shotgun_shot"] = "rifle_shot",
        ["shotgun_reload"] = "reload",
    };

    private AudioStream? Pick(string name)
    {
        var options = Streams(name);
        return options.Length == 0 ? null : options[_random.Next(options.Length)];
    }

    /// <summary>A copy of the sound set to loop (the imported file itself isn't changed).</summary>
    public AudioStream? Loop(string name) => Looping(name);

    private AudioStream? Looping(string name)
    {
        if (Pick(name)?.Duplicate() is not AudioStream stream) return null;
        switch (stream)
        {
            case AudioStreamWav wav:
                wav.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
                wav.LoopBegin = 0;
                wav.LoopEnd = (int)(wav.GetLength() * wav.MixRate);
                break;
            case AudioStreamOggVorbis ogg:
                ogg.Loop = true;
                break;
            case AudioStreamMP3 mp3:
                mp3.Loop = true;
                break;
        }
        return stream;
    }

    // ------------------------------------------------------------------ one-shots

    /// <summary>A sound the server sent (a <see cref="SoundId"/>), at a point in the world.</summary>
    public void PlayServerSound(int sound, Vector3 at)
    {
        if (!Enum.IsDefined(typeof(SoundId), sound)) return;
        var (name, db, pitch, distance) = Spec((SoundId)sound);
        if (name != "") Play3D(name, at, db, pitch, distance);
    }

    /// <summary>A sound at a point: louder close by, fading out by <paramref name="maxDistance"/>.</summary>
    public void Play3D(string name, Vector3 at, float db = 0, float pitchSpread = 0.05f, float maxDistance = 30, string bus = Sfx)
    {
        if (Pick(name) is not { } stream || !IsInsideTree()) return;
        var voice = Voice();
        if (voice == null) return;
        voice.Stream = stream;
        voice.Bus = bus;
        voice.VolumeDb = db;
        voice.PitchScale = 1 + (float)(_random.NextDouble() * 2 - 1) * pitchSpread;
        voice.MaxDistance = maxDistance;
        voice.UnitSize = Mathf.Max(2, maxDistance / 12);
        voice.GlobalPosition = at;
        voice.Play();
        Track(voice, name);
    }

    /// <summary>A sound with no place (menus, alerts, what happens to you).</summary>
    public void Play2D(string name, float db = 0, string bus = Ui)
    {
        if (Pick(name) is not { } stream) return;
        var player = new AudioStreamPlayer { Stream = stream, Bus = bus, VolumeDb = db };
        AddChild(player);
        player.Finished += player.QueueFree;
        player.Play();
        Track(player, name);
    }

    // ------------------------------------------------------------------ what's playing (for the F1 monitor)

    private readonly Dictionary<string, string> _sources = new(); // sound name -> "real", "placeholder", "stand-in: x"
    private readonly Dictionary<Node, (string Name, double LastHeard)> _tracked = new();
    private SoundMonitor? _monitor;

    /// <summary>Short sounds stay on the monitor this long after they end, so they can be read.</summary>
    private const double Linger = 1.5;

    /// <summary>Remembers which sound a player is playing (one-shots, loops), for the monitor.</summary>
    public void Track(Node player, string name)
    {
        Streams(name); // so its source is known
        _tracked[player] = (name, Time.GetTicksMsec() / 1000.0);
    }

    /// <summary>Everything playing now (or just ended): name, real or placeholder, distance from the camera (null: 2D), silent (faded out).</summary>
    public List<(string Name, string Source, float? Distance, bool Silent)> Playing()
    {
        var now = Time.GetTicksMsec() / 1000.0;
        var camera = GetViewport()?.GetCamera3D()?.GlobalPosition;
        var list = new List<(string, string, float?, bool)>();
        foreach (var (node, (name, lastHeard)) in _tracked.ToList())
        {
            if (!IsInstanceValid(node) || !node.IsInsideTree())
            {
                _tracked.Remove(node);
                continue;
            }
            var playing = node.Get("playing").AsBool();
            if (playing) _tracked[node] = (name, now);
            else if (now - lastHeard > Linger) continue;
            float? distance = node is Node3D n3 && camera is { } c ? n3.GlobalPosition.DistanceTo(c) : null;
            var silent = (float)node.Get("volume_db") < -50;
            list.Add((name, _sources.GetValueOrDefault(name, "?"), distance, silent));
        }
        return list;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.F1 }) return;
        ToggleMonitor();
        GetViewport().SetInputAsHandled();
    }

    private void ToggleMonitor()
    {
        if (_monitor != null)
        {
            _monitor.QueueFree();
            _monitor = null;
        }
        else
        {
            _monitor = new SoundMonitor { Name = "SoundMonitor" };
            AddChild(_monitor);
            GD.Print("[audio] " + SoundMonitor.Describe());
        }
    }

    /// <summary>A free 3D voice (or the one that has played longest, if all are busy).</summary>
    private AudioStreamPlayer3D? Voice()
    {
        foreach (var v in _voices)
            if (!v.Playing) return v;
        if (_voices.Count < MaxVoices)
        {
            var v = new AudioStreamPlayer3D { AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance, TopLevel = true };
            AddChild(v);
            _voices.Add(v);
            return v;
        }
        return _voices.MaxBy(v => v.GetPlaybackPosition());
    }

    // ------------------------------------------------------------------ footsteps

    private sealed class Walker
    {
        public required Node3D Node;
        public Vector3 Last;
        public float Travelled;
        public float Volume;
    }

    private readonly List<Walker> _walkers = new();

    /// <summary>Indoors (wooden floors) at a point? Set for a city; null: everywhere is the place's usual ground.</summary>
    public Func<Vector3, bool>? Indoors { get; set; }

    /// <summary>What the ground is outside: "stone" (city streets), "wood" (dungeon floors), "dirt" (the hub).</summary>
    private string _ground = "dirt";

    /// <summary>Footsteps for a character (a player, a resident, a monster), while it exists.</summary>
    public void TrackFootsteps(Node3D character, float volumeDb = 0)
    {
        if (Main.IsServer) return;
        _walkers.Add(new Walker { Node = character, Last = character.GlobalPosition, Volume = volumeDb });
    }

    private const float StepLength = 0.75f, HearStepsWithin = 25;

    private void UpdateFootsteps()
    {
        var listener = GetViewport()?.GetCamera3D()?.GlobalPosition;
        for (var i = _walkers.Count - 1; i >= 0; i--)
        {
            var w = _walkers[i];
            if (!IsInstanceValid(w.Node) || !w.Node.IsInsideTree())
            {
                _walkers.RemoveAt(i);
                continue;
            }
            var pos = w.Node.GlobalPosition;
            var moved = new Vector2(pos.X - w.Last.X, pos.Z - w.Last.Z).Length();
            w.Last = pos;
            if (moved > 3) continue; // a jump (spawned, teleported, corrected): not a step
            if (moved < 0.002f)
            {
                w.Travelled = MathF.Min(w.Travelled, StepLength * 0.5f); // the first step comes soon after starting
                continue;
            }
            w.Travelled += moved;
            if (w.Travelled < StepLength) continue;
            w.Travelled = 0;
            if (listener is { } l && l.DistanceTo(pos) > HearStepsWithin) continue;
            var surface = Indoors?.Invoke(pos) == true ? "wood" : _ground;
            Play3D("step_" + surface, pos + Vector3.Up * 0.05f, w.Volume, 0.1f, HearStepsWithin, Steps);
        }
    }

    // ------------------------------------------------------------------ ambience

    private AudioStreamPlayer _ambienceA = null!, _ambienceB = null!;
    private string _kind = "";
    private float _hour = -1;
    private readonly List<(AudioStreamPlayer3D Player, BuildingType Type)> _venues = new();

    private AudioStreamPlayer MakeLoopPlayer(string name)
    {
        var p = new AudioStreamPlayer { Name = name, Bus = Ambience, VolumeDb = -80 };
        AddChild(p);
        return p;
    }

    /// <summary>Entering a place: its ambience and ground. Call with the instance kind ("hub", "dungeon", "city").</summary>
    public void EnterPlace(string kind, Node3D? root, CityLayout? city)
    {
        _kind = kind;
        _hour = -1;
        Indoors = null;
        _venues.Clear();
        _ground = kind switch { "city" => "stone", "dungeon" => "wood", _ => "dirt" };
        SetLoop(_ambienceA, kind switch { "dungeon" => "amb_dungeon", "city" => "amb_city_day", _ => "amb_hub" });
        SetLoop(_ambienceB, kind == "city" ? "amb_city_night" : null);
        _ambienceA.VolumeDb = 0;
        _ambienceB.VolumeDb = -80;
        if (city != null && root != null)
        {
            Indoors = p => city.ParcelAt(p.X, p.Z) is { } parcel && parcel.Footprint.Contains(p.X, p.Z);
            // Music from every tavern's open door, while it's open.
            foreach (var parcel in city.Parcels.Where(p => p.Building == BuildingType.Tavern))
            {
                if (Looping("tavern_music") is not { } music) break;
                var player = new AudioStreamPlayer3D
                {
                    Name = $"TavernMusic{parcel.Id}", Stream = music, Bus = Music, MaxDistance = 35, UnitSize = 4,
                    VolumeDb = -80, Position = new Vector3(parcel.Door.X, 1.5f, parcel.Door.Z),
                };
                root.AddChild(player);
                player.Play((float)(_random.NextDouble() * 10));
                Track(player, "tavern_music");
                _venues.Add((player, parcel.Building));
            }
        }
        Play2D("travel", -6);
    }

    /// <summary>The city's time (drives the day/night ambience and when the taverns play).</summary>
    public void SetCityHour(float hour) => _hour = hour;

    private void SetLoop(AudioStreamPlayer player, string? name)
    {
        player.Stop();
        player.Stream = name == null ? null : Looping(name);
        if (player.Stream != null)
        {
            player.Play();
            Track(player, name!);
        }
    }

    private void UpdateAmbience(float delta)
    {
        if (_kind != "city" || _hour < 0) return;
        var day = CityLighting.Daylight(_hour);
        Fade(_ambienceA, Mathf.LinearToDb(Mathf.Max(0.001f, day)), delta);
        Fade(_ambienceB, Mathf.LinearToDb(Mathf.Max(0.001f, 1 - day)), delta);
        foreach (var (player, type) in _venues)
            if (IsInstanceValid(player))
                Fade(player, Stealth.IsOpen(type, _hour) ? 0 : -80, delta);
    }

    private static void Fade(Node player, float targetDb, float delta)
    {
        var current = (float)player.Get("volume_db");
        player.Set("volume_db", Mathf.Lerp(current, targetDb, 1 - Mathf.Exp(-2f * delta)));
    }

    public override void _Process(double delta)
    {
        UpdateFootsteps();
        UpdateAmbience((float)delta);
    }
}
