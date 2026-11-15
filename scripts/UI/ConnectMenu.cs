using System;
using ExploreTogether.Core;
using Godot;

namespace ExploreTogether.UI;

/// <summary>Minimal connect screen: display name, color and server address. Built in code.</summary>
public partial class ConnectMenu : CanvasLayer
{
    public event Action<string, Color, string>? ConnectRequested; // name, color, address

    private LineEdit _name = null!;
    private ColorPickerButton _color = null!;
    private LineEdit _address = null!;
    private Button _connect = null!;
    private Label _status = null!;

    public override void _Ready()
    {
        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(center);

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(360, 0) };
        center.AddChild(panel);

        var margin = new MarginContainer();
        foreach (var side in new[] { "left", "right", "top", "bottom" })
            margin.AddThemeConstantOverride($"margin_{side}", 16);
        panel.AddChild(margin);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        margin.AddChild(box);

        box.AddChild(new Label { Text = GameInfo.Name, HorizontalAlignment = HorizontalAlignment.Center });
        box.AddChild(new Label { Text = "Name" });
        _name = new LineEdit { Text = CmdArgs.Get("name", "Player"), MaxLength = 24 };
        box.AddChild(_name);
        box.AddChild(new Label { Text = "Color" });
        _color = new ColorPickerButton
        {
            Color = PlayerLook.ParseColor(CmdArgs.Get("color"), PlayerLook.DefaultColor),
            EditAlpha = false,
            CustomMinimumSize = new Vector2(0, 32),
        };
        box.AddChild(_color);
        box.AddChild(new Label { Text = "Server address" });
        _address = new LineEdit { Text = $"127.0.0.1:{CmdArgs.GetInt("port", GameInfo.DefaultPort)}" };
        box.AddChild(_address);
        _connect = new Button { Text = "Connect" };
        box.AddChild(_connect);
        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        box.AddChild(_status);

        _connect.Pressed += Submit;
        _address.TextSubmitted += _ => Submit();
        _name.TextSubmitted += _ => Submit();
    }

    private void Submit() => ConnectRequested?.Invoke(_name.Text, _color.Color, _address.Text);

    public void SetBusy(bool busy, string status)
    {
        _connect.Disabled = busy;
        _status.Text = status;
    }
}
