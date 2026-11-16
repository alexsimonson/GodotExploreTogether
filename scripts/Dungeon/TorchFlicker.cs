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
