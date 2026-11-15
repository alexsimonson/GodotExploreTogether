using Godot;

namespace ExploreTogether.UI;

/// <summary>Small helpers for building simple menus in code.</summary>
public static class UiKit
{
    /// <summary>Adds a centered panel to the layer and returns the vertical box to fill.</summary>
    public static VBoxContainer CenteredPanel(CanvasLayer layer, float minWidth = 380)
    {
        var center = new CenterContainer();
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(center);

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(minWidth, 0) };
        center.AddChild(panel);

        var margin = new MarginContainer();
        foreach (var side in new[] { "left", "right", "top", "bottom" })
            margin.AddThemeConstantOverride($"margin_{side}", 16);
        panel.AddChild(margin);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        margin.AddChild(box);
        return box;
    }

    public static Label Title(string text) =>
        new() { Text = text, HorizontalAlignment = HorizontalAlignment.Center };

    public static Label Status() =>
        new() { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(0, 24) };

    public static string FormatPlayTime(double seconds)
    {
        var t = System.TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m {t.Seconds}s";
    }
}
