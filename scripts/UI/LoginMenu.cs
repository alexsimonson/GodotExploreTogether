using System;
using ExploreTogether.Core;
using Godot;

namespace ExploreTogether.UI;

/// <summary>Server address + username + password, with Log in and Register buttons.</summary>
public partial class LoginMenu : CanvasLayer
{
    public enum AuthAction { Login, Register }

    /// <summary>action, address, username, password</summary>
    public event Action<AuthAction, string, string, string>? Submitted;

    private LineEdit _address = null!;
    private LineEdit _username = null!;
    private LineEdit _password = null!;
    private LineEdit _invite = null!;

    /// <summary>The invite code typed in (only sent when registering; a public server needs one).</summary>
    public string InviteCode => _invite.Text.StripEdges();
    private Button _login = null!;
    private Button _register = null!;
    private Label _status = null!;

    public override void _Ready()
    {
        var box = UiKit.CenteredPanel(this);
        box.AddChild(UiKit.Title(GameInfo.Name));

        box.AddChild(new Label { Text = "Server address" });
        // --connect ADDRESS pre-fills it (e.g. a shortcut for a friend's server).
        _address = new LineEdit { Text = CmdArgs.Get("connect", $"127.0.0.1:{CmdArgs.GetInt("port", GameInfo.DefaultPort)}") };
        box.AddChild(_address);

        box.AddChild(new Label { Text = "Username" });
        _username = new LineEdit { Text = CmdArgs.Get("user"), MaxLength = GameRules.UsernameMax };
        box.AddChild(_username);

        box.AddChild(new Label { Text = "Password" });
        _password = new LineEdit { Secret = true, MaxLength = GameRules.PasswordMax };
        box.AddChild(_password);

        box.AddChild(new Label { Text = "Invite code (to register on a friend's server)" });
        _invite = new LineEdit { Text = CmdArgs.Get("invite"), PlaceholderText = "leave empty on your own server" };
        box.AddChild(_invite);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 8);
        _login = new Button { Text = "Log in", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _register = new Button { Text = "Register", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        buttons.AddChild(_login);
        buttons.AddChild(_register);
        box.AddChild(buttons);

        _status = UiKit.Status();
        box.AddChild(_status);

        _login.Pressed += () => Submit(AuthAction.Login);
        _register.Pressed += () => Submit(AuthAction.Register);
        _password.TextSubmitted += _ => Submit(AuthAction.Login);
    }

    private void Submit(AuthAction action)
    {
        var username = _username.Text.StripEdges();
        var error = GameRules.ValidateUsername(username) ?? GameRules.ValidatePassword(_password.Text);
        if (error != null && action == AuthAction.Register)
        {
            SetStatus(error);
            return;
        }
        Submitted?.Invoke(action, _address.Text, username, _password.Text);
    }

    public void SetBusy(bool busy, string status)
    {
        _login.Disabled = busy;
        _register.Disabled = busy;
        SetStatus(status);
    }

    public void SetStatus(string status) => _status.Text = status;
}
