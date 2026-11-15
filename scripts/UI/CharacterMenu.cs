using System;
using System.Collections.Generic;
using ExploreTogether.Core;
using ExploreTogether.Net;
using Godot;

namespace ExploreTogether.UI;

/// <summary>Character select: play an existing character or create a new one (name + color).</summary>
public partial class CharacterMenu : CanvasLayer
{
    public event Action<long>? PlayRequested;          // character id
    public event Action<string, Color>? CreateRequested; // name, color
    public event Action? LogoutRequested;

    private VBoxContainer _list = null!;
    private LineEdit _newName = null!;
    private ColorPickerButton _newColor = null!;
    private Button _create = null!;
    private Label _status = null!;
    private readonly List<Button> _playButtons = new();

    public override void _Ready()
    {
        var box = UiKit.CenteredPanel(this, 420);
        box.AddChild(UiKit.Title("Choose a character"));

        _list = new VBoxContainer();
        _list.AddThemeConstantOverride("separation", 6);
        box.AddChild(_list);

        box.AddChild(new HSeparator());
        box.AddChild(new Label { Text = "New character" });

        var createRow = new HBoxContainer();
        createRow.AddThemeConstantOverride("separation", 8);
        _newName = new LineEdit
        {
            PlaceholderText = "Name",
            MaxLength = GameRules.CharacterNameMax,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _newColor = new ColorPickerButton
        {
            Color = PlayerLook.ParseColor(CmdArgs.Get("color"), PlayerLook.DefaultColor),
            EditAlpha = false,
            CustomMinimumSize = new Vector2(48, 0),
        };
        _create = new Button { Text = "Create" };
        createRow.AddChild(_newName);
        createRow.AddChild(_newColor);
        createRow.AddChild(_create);
        box.AddChild(createRow);

        _status = UiKit.Status();
        box.AddChild(_status);

        var logout = new Button { Text = "Log out" };
        box.AddChild(logout);

        _create.Pressed += SubmitCreate;
        _newName.TextSubmitted += _ => SubmitCreate();
        logout.Pressed += () => LogoutRequested?.Invoke();
    }

    public void SetCharacters(IReadOnlyList<CharacterSummary> characters)
    {
        foreach (var child in _list.GetChildren()) child.QueueFree();
        _playButtons.Clear();

        if (characters.Count == 0)
            _list.AddChild(new Label { Text = "No characters yet. Create one below." });

        foreach (var c in characters)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            row.AddChild(new ColorRect { Color = c.Color, CustomMinimumSize = new Vector2(20, 20) });
            row.AddChild(new Label
            {
                Text = $"{c.Name}   ({UiKit.FormatPlayTime(c.PlayTimeSeconds)} played)",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            });
            var play = new Button { Text = "Play" };
            var id = c.Id;
            play.Pressed += () => PlayRequested?.Invoke(id);
            row.AddChild(play);
            _playButtons.Add(play);
            _list.AddChild(row);
        }
        _create.Disabled = characters.Count >= GameRules.MaxCharactersPerAccount;
    }

    private void SubmitCreate()
    {
        var name = _newName.Text.StripEdges();
        var error = GameRules.ValidateCharacterName(name);
        if (error != null)
        {
            SetStatus(error);
            return;
        }
        CreateRequested?.Invoke(name, _newColor.Color);
        _newName.Text = "";
    }

    public void SetBusy(bool busy, string status = "")
    {
        foreach (var b in _playButtons) b.Disabled = busy;
        SetStatus(status);
    }

    public void SetStatus(string status) => _status.Text = status;
}
