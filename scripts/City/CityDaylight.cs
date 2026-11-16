using ExploreTogether.Core.City;
using Godot;

namespace ExploreTogether.City;

/// <summary>
/// Client: sun, sky light and ambient follow the city clock (sunrise about 6:00, sunset about 18:00), and the
/// street lamps come on at dusk. Nights are dark: away from lamps and lit windows there's only faint moonlight
/// (the same curve as <see cref="CityLighting.Daylight"/>, which the server uses for what residents can see).
/// Added to every city level by <see cref="CityBuilder"/>; only clients call <see cref="SetHour"/>.
/// </summary>
public partial class CityDaylight : Node
{
    private DirectionalLight3D _sun = null!;
    private Godot.Environment _environment = null!;

    public void Init(DirectionalLight3D sun, Godot.Environment environment) => (_sun, _environment) = (sun, environment);

    public void SetHour(float hour)
    {
        // Sun angle: rises in the east at 6, highest at 12 (about 65 degrees), sets in the west at 18.
        var dayAngle = (hour - 6f) / 12f * Mathf.Pi; // 0 at sunrise, pi at sunset
        var elevation = Mathf.Sin(dayAngle) * 65f;
        var daylight = CityLighting.Daylight(hour);
        _sun.RotationDegrees = new Vector3(-Mathf.Max(elevation, 5f), 90f - Mathf.RadToDeg(dayAngle), 0);
        _sun.LightEnergy = 1.3f * daylight;
        _sun.LightColor = new Color(1f, 0.75f + 0.25f * daylight, 0.55f + 0.45f * daylight);
        _environment.AmbientLightEnergy = 0.035f + 0.465f * daylight;
        _environment.AmbientLightColor = new Color(0.70f, 0.75f, 0.85f).Lerp(new Color(0.30f, 0.38f, 0.65f), 1 - daylight);
        _environment.BackgroundEnergyMultiplier = 0.03f + 0.97f * daylight;
        if (GetParent()?.GetNodeOrNull<Node3D>("Lamps") is { } lamps) lamps.Visible = CityLighting.LampsOn(hour);
    }
}
