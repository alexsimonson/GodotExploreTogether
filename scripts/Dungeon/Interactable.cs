using Godot;

namespace ExploreTogether.Dungeon;

/// <summary>
/// Something a player can look at and press E on (port of the original IInteraction).
/// The client only detects it and sends a request with the InteractionId. The server
/// checks the player is close enough and decides what happens.
/// </summary>
public partial class Interactable : StaticBody3D
{
    /// <summary>Client raycast reach from the camera.</summary>
    public const float ReachDistance = 3.5f;

    /// <summary>Server-side limit (player feet to object origin), a bit more lenient than the ray.</summary>
    public const float ServerMaxDistance = 5f;

    [Export] public string InteractionId { get; set; } = "";
    [Export] public string Prompt { get; set; } = "Interact";
}
