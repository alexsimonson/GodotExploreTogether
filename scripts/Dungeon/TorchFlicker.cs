using Godot;

namespace ExploreTogether.Dungeon;

/// <summary>Gentle random flicker for a torch light (client only; the server doesn't render).</summary>
public partial class TorchFlicker : OmniLight3D
{
    private float _baseEnergy;
    private float _target;
    private double _timeLeft;

    public override void _Ready()
    {
        _baseEnergy = LightEnergy;
        _target = _baseEnergy;
        SetProcess(!Main.IsServer);
        if (!Main.IsServer) CallDeferred(MethodName.AddCrackle);
    }

    /// <summary>A quiet fire crackle, heard only up close.</summary>
    private void AddCrackle()
    {
        if (Audio.GameAudio.Instance?.Loop("torch_crackle") is not { } stream) return;
        var player = new AudioStreamPlayer3D { Stream = stream, Bus = Audio.GameAudio.Ambience, MaxDistance = 7, UnitSize = 1.5f, VolumeDb = -6 };
        AddChild(player);
        player.Play((float)GD.RandRange(0, 2));
        Audio.GameAudio.Instance?.Track(player, "torch_crackle");
    }

    public override void _Process(double delta)
    {
        if ((_timeLeft -= delta) <= 0)
        {
            _timeLeft = GD.RandRange(0.05, 0.15);
            _target = _baseEnergy * (float)GD.RandRange(0.8, 1.15);
        }
        LightEnergy = Mathf.Lerp(LightEnergy, _target, 1f - Mathf.Exp(-12f * (float)delta));
    }
}
