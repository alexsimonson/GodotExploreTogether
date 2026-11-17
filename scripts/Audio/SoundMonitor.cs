using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;

namespace ExploreTogether.Audio;

/// <summary>
/// F1: an overlay listing every sound playing right now, grouped by name: what it's meant to sound like, whether
/// it's a real file or a generated placeholder, how many are playing, and how far the nearest one is. Short sounds
/// stay listed for a moment after they end, so clicks and thuds can be read. Opening it also prints the list to
/// the log. See docs/actionables/AUDIO-REPORT.md for the full list of sounds.
/// </summary>
public partial class SoundMonitor : CanvasLayer
{
    /// <summary>What each sound is meant to imitate (shown next to its name).</summary>
    public static readonly Dictionary<string, string> Meant = new()
    {
        ["gunshot"] = "pistol shot",
        ["shotgun_shot"] = "shotgun blast (heavy, boomy)",
        ["rifle_shot"] = "rifle shot",
        ["reload"] = "pistol magazine reload",
        ["shotgun_reload"] = "shells sliding in / pump rack",
        ["dry_fire"] = "empty gun: trigger click",
        ["swing"] = "weapon whoosh through the air",
        ["hit_flesh"] = "blade or blow landing on a body",
        ["hit_blunt"] = "blunt knockout blow / punch",
        ["chop"] = "axe biting into wood",
        ["magic_ice"] = "crystalline frost spell",
        ["magic_blood"] = "wet, low, ominous blood spell",
        ["enemy_attack"] = "monster growl / snarl as it strikes",
        ["enemy_hurt"] = "monster pained grunt",
        ["enemy_death"] = "monster death cry",
        ["player_hurt"] = "player grunt of pain",
        ["player_death"] = "player death cry",
        ["npc_hurt"] = "resident cry of pain",
        ["npc_death"] = "resident death cry",
        ["scream"] = "terrified scream",
        ["grab"] = "scuffle: cloth and a grunt",
        ["break_free"] = "struggling free",
        ["door_open"] = "wooden door creaking open",
        ["door_close"] = "door shutting, latch clicks",
        ["door_lock"] = "key turning / deadbolt",
        ["door_kick"] = "heavy kick against a wooden door",
        ["door_break"] = "door splintering in",
        ["lockpick"] = "quiet metal scratching in a lock",
        ["lockpick_break"] = "lockpick snapping",
        ["item_pickup"] = "picking something up",
        ["item_drop"] = "something dropped on the floor",
        ["exit_open"] = "stone door grinding open",
        ["travel"] = "whoosh through a gate",
        ["furniture_break"] = "chair smashing to pieces",
        ["furniture_grab"] = "chair scraped off the floor",
        ["flashlight_click"] = "flashlight switch click",
        ["glass_break"] = "bulb pop and glass tinkle",
        ["light_switch"] = "wall light switch",
        ["fusebox_bang"] = "metal box door clanging",
        ["breaker"] = "breaker switch clack",
        ["sparks"] = "electrical crackle, hum dying",
        ["main_breaker"] = "big lever clunk, generator winding down",
        ["zombie_groan"] = "zombie moan",
        ["zombie_bite"] = "zombie bite: snarl and tearing",
        ["zombie_death"] = "zombie going down for good: wet thud, last gurgle",
        ["zombie_bash"] = "fists pounding on a wooden door",
        ["step_stone"] = "footstep on stone",
        ["step_wood"] = "footstep on wooden floor",
        ["step_dirt"] = "footstep on dirt / grass",
        ["amb_hub"] = "outdoor wind and birds",
        ["amb_dungeon"] = "low drone and dripping",
        ["amb_city_day"] = "town murmur, birds, carts",
        ["amb_city_night"] = "crickets, wind, a distant dog",
        ["tavern_music"] = "lively folk music from inside",
        ["torch_crackle"] = "soft fire crackle",
        ["alert_suspicious"] = "stinger: you've been noticed",
        ["alert_alarmed"] = "stinger: alarm raised",
        ["ui_open"] = "inventory opening",
        ["ui_close"] = "inventory closing",
    };

    private Label _label = null!;
    private double _refresh;

    public override void _Ready()
    {
        Layer = 10;
        var panel = new PanelContainer { Position = new Vector2(10, 60), MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.6f), ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 6, ContentMarginBottom = 6 });
        AddChild(panel);
        _label = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        _label.AddThemeFontSizeOverride("font_size", 13);
        panel.AddChild(_label);
    }

    public override void _Process(double delta)
    {
        if ((_refresh -= delta) > 0) return;
        _refresh = 0.2;
        _label.Text = Describe();
    }

    /// <summary>The list as text (also what goes to the log when the monitor opens).</summary>
    public static string Describe()
    {
        if (GameAudio.Instance is not { } audio) return "No audio.";
        var rows = audio.Playing().GroupBy(p => (p.Name, p.Source))
            .Select(g => (g.Key.Name, g.Key.Source, Count: g.Count(), Nearest: g.Min(p => p.Distance), Silent: g.All(p => p.Silent)))
            .OrderBy(r => r.Silent).ThenBy(r => r.Nearest ?? -1).ToList();
        var sb = new StringBuilder($"Sounds playing [F1 to hide]  ({rows.Sum(r => r.Count)})\n");
        if (rows.Count == 0) sb.Append("  (nothing)\n");
        foreach (var r in rows)
        {
            var meant = Meant.TryGetValue(r.Name, out var m) ? m : "?";
            var where = r.Nearest is { } d ? $"{d:0} m" : "2D";
            sb.Append($"  {r.Name}{(r.Count > 1 ? $" x{r.Count}" : "")}  - {meant}  [{r.Source}, {where}{(r.Silent ? ", silent" : "")}]\n");
        }
        return sb.ToString().TrimEnd();
    }
}
