using Godot;

namespace ExploreTogether;

/// <summary>Player appearance helpers shared by client UI and server validation.</summary>
public static class PlayerLook
{
    public static readonly Color DefaultColor = new(0.25f, 0.55f, 0.9f);

    /// <summary>Parses "#rrggbb", "rrggbb" or a named color ("green", "royal_blue").</summary>
    public static Color ParseColor(string text, Color fallback) =>
        string.IsNullOrWhiteSpace(text) ? fallback : Color.FromString(text.Trim(), fallback);

    /// <summary>Server-side cleanup of a client-supplied color: opaque and within range.</summary>
    public static Color Sanitize(Color c) =>
        new(Mathf.Clamp(c.R, 0f, 1f), Mathf.Clamp(c.G, 0f, 1f), Mathf.Clamp(c.B, 0f, 1f), 1f);
}
