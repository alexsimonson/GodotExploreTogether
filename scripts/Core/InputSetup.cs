using Godot;

namespace ExploreTogether;

/// <summary>Registers the default input actions in code, so the key bindings live in source control as C#.</summary>
public static class InputSetup
{
    public const string MoveForward = "move_forward";
    public const string MoveBack = "move_back";
    public const string MoveLeft = "move_left";
    public const string MoveRight = "move_right";
    public const string Jump = "jump";
    public const string Sprint = "sprint";
    public const string Interact = "interact";

    public static void EnsureActions()
    {
        Add(MoveForward, Key.W);
        Add(MoveBack, Key.S);
        Add(MoveLeft, Key.A);
        Add(MoveRight, Key.D);
        Add(Jump, Key.Space);
        Add(Sprint, Key.Shift);
        Add(Interact, Key.E);
    }

    private static void Add(string action, Key key)
    {
        if (InputMap.HasAction(action)) return;
        InputMap.AddAction(action);
        InputMap.ActionAddEvent(action, new InputEventKey { PhysicalKeycode = key });
    }
}
