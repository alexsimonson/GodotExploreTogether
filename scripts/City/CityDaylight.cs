using Godot;

namespace ExploreTogether.City;

/// <summary>
/// Client: sun, sky light and ambient follow the city clock (sunrise about 6:00, sunset about 18:00, dark nights).
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
        var daylight = Mathf.Clamp(Mathf.Sin(dayAngle) * 2.5f, 0f, 1f); // fades over the first/last hour or so
        _sun.RotationDegrees = new Vector3(-Mathf.Max(elevation, 5f), 90f - Mathf.RadToDeg(dayAngle), 0);
        _sun.LightEnergy = 1.3f * daylight;
        _sun.LightColor = new Color(1f, 0.75f + 0.25f * daylight, 0.55f + 0.45f * daylight);
        _environment.AmbientLightEnergy = 0.12f + 0.38f * daylight;
        _environment.AmbientLightColor = new Color(0.70f, 0.75f, 0.85f).Lerp(new Color(0.35f, 0.42f, 0.65f), 1 - daylight);
        _environment.BackgroundEnergyMultiplier = 0.08f + 0.92f * daylight;
    }
}
