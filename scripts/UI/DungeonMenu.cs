using System;
using System.Collections.Generic;
using ExploreTogether.Net;
using Godot;

namespace ExploreTogether.UI;

/// <summary>
/// Opened from the hub's dungeon entrance: join a running dungeon (even mid-run) or start a new one.
/// New dungeons use the original generator settings: grid width/height and dungeon length (max nodes).
/// </summary>
public partial class DungeonMenu : CanvasLayer
{
    public event Action<int, int, int>? CreateRequested; // width, height, length
    public event Action<int>? JoinRequested;            // instance id
    public event Action? Closed;

    private VBoxContainer _list = null!;
    private SpinBox _width = null!, _height = null!, _length = null!;

    public override void _Ready()
    {
        var box = UiKit.CenteredPanel(this, 460);
        box.AddChild(UiKit.Title("Dungeon Entrance"));

        box.AddChild(new Label { Text = "Running dungeons" });
        _list = new VBoxContainer();
        _list.AddThemeConstantOverride("separation", 6);
        box.AddChild(_list);

        box.AddChild(new HSeparator());
        box.AddChild(new Label { Text = "New dungeon" });
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 12);
        _width = AddSpin(grid, "Grid width", 1, 30, 5);
        _height = AddSpin(grid, "Grid height", 1, 30, 5);
        _length = AddSpin(grid, "Dungeon length", 1, 1000, 400);
        box.AddChild(grid);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 8);
        var create = new Button { Text = "Start new dungeon", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var cancel = new Button { Text = "Cancel" };
        buttons.AddChild(create);
        buttons.AddChild(cancel);
        box.AddChild(buttons);

        create.Pressed += () => CreateRequested?.Invoke((int)_width.Value, (int)_height.Value, (int)_length.Value);
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

    public void Open(IReadOnlyList<DungeonSummary> dungeons)
    {
        foreach (var child in _list.GetChildren()) child.QueueFree();
        if (dungeons.Count == 0)
            _list.AddChild(new Label { Text = "None yet." });

        foreach (var d in dungeons)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            var who = d.Players.Length == 0 ? "empty" : string.Join(", ", d.Players);
            row.AddChild(new Label
            {
                Text = $"#{d.Id}  {d.CreatedBy}'s  {d.Width}x{d.Height}  ({who})",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                ClipText = true,
            });
            var join = new Button { Text = "Join" };
            var id = d.Id;
            join.Pressed += () => JoinRequested?.Invoke(id);
            row.AddChild(join);
            _list.AddChild(row);
        }
        Visible = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    public void Close()
    {
        if (!Visible) return;
        Visible = false;
        Input.MouseMode = Input.MouseModeEnum.Captured;
        Closed?.Invoke();
    }

    private static SpinBox AddSpin(GridContainer grid, string label, int min, int max, int value)
    {
        grid.AddChild(new Label { Text = label });
        var spin = new SpinBox { MinValue = min, MaxValue = max, Value = value, Step = 1, CustomMinimumSize = new Vector2(120, 0) };
        grid.AddChild(spin);
        return spin;
    }
}
