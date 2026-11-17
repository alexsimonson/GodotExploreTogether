using System;
using System.Collections.Generic;
using ExploreTogether.Core.City;
using ExploreTogether.Net;
using Godot;

namespace ExploreTogether.UI;

/// <summary>
/// Opened from the hub's City Gate: join a running city, or make a new one. The options (size, the time it starts
/// at, residents, how long a day lasts, seed) are for trying things out: midnight in a big city, a quiet village at
/// noon, a fast clock to watch a whole day go by. The server sends its defaults (its --city-* args).
/// </summary>
public partial class CityMenu : CanvasLayer
{
    public event Action<CitySettings>? CreateRequested;
    public event Action<int>? JoinRequested; // instance id; 0 = wherever the others are (or a new default city)
    public event Action? Closed;

    private VBoxContainer _list = null!;
    private SpinBox _size = null!, _hour = null!, _people = null!, _dayMinutes = null!, _seed = null!, _zombies = null!;
    private Label _peopleHint = null!;

    public override void _Ready()
    {
        var box = UiKit.CenteredPanel(this, 500);
        box.AddChild(UiKit.Title("City Gate"));

        box.AddChild(new Label { Text = "Running cities" });
        _list = new VBoxContainer();
        _list.AddThemeConstantOverride("separation", 6);
        box.AddChild(_list);

        box.AddChild(new HSeparator());
        box.AddChild(new Label { Text = "New city" });
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 12);
        _size = AddSpin(grid, "Size (tiles per side)", 1, 20, 5, 1);
        _hour = AddSpin(grid, "Start time (hour)", 0, 23.75, 0, 0.25);
        _people = AddSpin(grid, "People (0 = automatic)", 0, 1000, 0, 1);
        _dayMinutes = AddSpin(grid, "Real minutes per day", 1, 1440, 48, 1);
        _seed = AddSpin(grid, "Seed (0 = random)", 0, int.MaxValue, 0, 1);
        _zombies = AddSpin(grid, "Zombies (0 = none)", 0, 200, 0, 1);
        box.AddChild(grid);
        _peopleHint = new Label { Modulate = new Color(1, 1, 1, 0.6f) };
        box.AddChild(_peopleHint);
        _size.ValueChanged += _ => UpdateHint();
        _people.ValueChanged += _ => UpdateHint();
        _hour.ValueChanged += _ => UpdateHint();

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 8);
        var create = new Button { Text = "Start new city", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var cancel = new Button { Text = "Cancel" };
        buttons.AddChild(create);
        buttons.AddChild(cancel);
        box.AddChild(buttons);

        create.Pressed += () => CreateRequested?.Invoke(new CitySettings((int)_size.Value, (float)_hour.Value,
            (int)_people.Value > 0 ? (int)_people.Value : -1, (float)_dayMinutes.Value, (int)_seed.Value, (int)_zombies.Value));
        cancel.Pressed += Close;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Visible && @event.IsActionPressed("ui_cancel"))
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }

    /// <param name="defaults">The server's defaults; applied the first time only (afterwards the last choices stay).</param>
    public void Open(IReadOnlyList<CitySummary> cities, CitySettings defaults)
    {
        if (!_opened)
        {
            _opened = true;
            _size.Value = defaults.Size;
            _hour.Value = defaults.StartHour;
            _people.Value = Math.Max(0, defaults.People);
            _dayMinutes.Value = defaults.DayMinutes;
            _seed.Value = defaults.Seed;
            _zombies.Value = defaults.Zombies;
        }
        UpdateHint();
        foreach (var child in _list.GetChildren()) child.QueueFree();
        if (cities.Count == 0)
            _list.AddChild(new Label { Text = "None yet." });

        foreach (var c in cities)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            var who = c.Players.Length == 0 ? "empty" : string.Join(", ", c.Players);
            row.AddChild(new Label
            {
                Text = $"#{c.Id}  {c.CreatedBy}'s  {c.Size}x{c.Size}, {c.People} people, {CityClock.Format(c.Hour)}  ({who})",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                ClipText = true,
            });
            var join = new Button { Text = "Join" };
            var id = c.Id;
            join.Pressed += () => JoinRequested?.Invoke(id);
            row.AddChild(join);
            _list.AddChild(row);
        }
        Visible = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    private bool _opened;

    private void UpdateHint()
    {
        var size = (int)_size.Value;
        var people = (int)_people.Value > 0 ? (int)_people.Value : (int)Math.Round(size * size * CityPopulation.PerTile);
        _peopleHint.Text = $"{people} residents, starting at {CityClock.Format((float)_hour.Value)}.";
    }

    public void Close()
    {
        if (!Visible) return;
        Visible = false;
        Input.MouseMode = Input.MouseModeEnum.Captured;
        Closed?.Invoke();
    }

    private static SpinBox AddSpin(GridContainer grid, string label, double min, double max, double value, double step)
    {
        grid.AddChild(new Label { Text = label });
        var spin = new SpinBox { MinValue = min, MaxValue = max, Value = value, Step = step, CustomMinimumSize = new Vector2(120, 0) };
        grid.AddChild(spin);
        return spin;
    }
}
