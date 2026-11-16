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
    public const string LockDoor = "lock_door";
    public const string DragBody = "drag_body";
    public const string Crouch = "crouch";
    /// <summary>Grab a standing resident (hold on to choke them out), or struggle free when held.</summary>
    public const string Grab = "grab";
    public const string KickDoor = "kick_door";

    public static void EnsureActions()
    {
        Add(MoveForward, Key.W);
        Add(MoveBack, Key.S);
        Add(MoveLeft, Key.A);
        Add(MoveRight, Key.D);
        Add(Jump, Key.Space);
        Add(Sprint, Key.Shift);
        Add(Interact, Key.E);
        Add(LockDoor, Key.L);
        Add(DragBody, Key.G);
        Add(Crouch, Key.C);
        Add(Grab, Key.F);
        Add(KickDoor, Key.K);
    }

    private static void Add(string action, Key key)
    {
        if (InputMap.HasAction(action)) return;
        InputMap.AddAction(action);
        InputMap.ActionAddEvent(action, new InputEventKey { PhysicalKeycode = key });
    }
}
