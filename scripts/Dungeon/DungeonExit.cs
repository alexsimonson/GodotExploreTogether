using Godot;

namespace ExploreTogether.Dungeon;

/// <summary>
/// The dungeon's exit (port of the original Objective at the last generated node). Locked until a
/// player uses the Dungeon Pass on it; then it stays open for everyone in the instance, and each
/// player escapes by interacting with it. State lives on the server; clients get it via InstanceState.
/// </summary>
public partial class DungeonExit : Interactable
{
    public const string Id = "dungeon_exit";

    public bool IsOpen { get; private set; }

    public override void _Ready()
    {
        InteractionId = Id;
        SetOpen(false);
    }

    public void SetOpen(bool open)
    {
        IsOpen = open;
        Prompt = open ? "Escape the dungeon" : "Exit (locked: use the Dungeon Pass)";
        GetNode<Node3D>("Door").Visible = !open;
        GetNode<Node3D>("Portal").Visible = open;
        GetNode<OmniLight3D>("Glow").Visible = open;
        GetNode<Label3D>("Label").Text = open ? "Exit (open)" : "Exit (locked)";
    }
}
