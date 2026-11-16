using ExploreTogether.Core.City;
using Godot;

namespace ExploreTogether.City;

/// <summary>
/// Client: the sky, sun, moon, ambient light and fog follow the city clock (sunrise about 6:00, sunset about 18:00),
/// and the street lamps and porch lights come on at dusk.
///
/// Nights are dark, with some shape to them:
/// - a faint, cool moon (the same directional light, turned and dimmed) casts soft shadows, so buildings read;
/// - a deep blue ambient and a navy sky, and a thin bluish fog that swallows distant streets;
/// - warm pools under the lamps, porch lights and lit windows (they glow, see CityBuilder's environment).
/// The daylight curve is <see cref="CityLighting.Daylight"/>, which the server also uses for what residents see.
/// Added to every city level by <see cref="CityBuilder"/>; only clients call <see cref="SetHour"/>.
/// </summary>
public partial class CityDaylight : Node
{
    private DirectionalLight3D _sun = null!;
    private Godot.Environment _environment = null!;

    private static readonly Color DaySky = new(0.38f, 0.45f, 0.55f), NightSky = new(0.012f, 0.018f, 0.045f);
    private static readonly Color DayHorizon = new(0.65f, 0.67f, 0.7f), NightHorizon = new(0.04f, 0.05f, 0.09f);
    private static readonly Color DayAmbient = new(0.70f, 0.75f, 0.85f), NightAmbient = new(0.22f, 0.28f, 0.48f);
    private static readonly Color DayFog = new(0.72f, 0.77f, 0.86f), NightFog = new(0.035f, 0.045f, 0.08f);
    private static readonly Color Moonlight = new(0.55f, 0.65f, 1f);

    public void Init(DirectionalLight3D sun, Godot.Environment environment) => (_sun, _environment) = (sun, environment);

    public void SetHour(float hour)
    {
        var daylight = CityLighting.Daylight(hour);
        var night = 1 - daylight;

        // Sun by day (rising in the east at 6, highest at 12, setting at 18); a dim moon, high in the south-west, by night.
        if (daylight > 0.12f)
        {
            var dayAngle = (hour - 6f) / 12f * Mathf.Pi;
            var elevation = Mathf.Sin(dayAngle) * 65f;
            _sun.RotationDegrees = new Vector3(-Mathf.Max(elevation, 5f), 90f - Mathf.RadToDeg(dayAngle), 0);
            _sun.LightEnergy = 0.85f * daylight;
            _sun.LightColor = new Color(1f, 0.75f + 0.25f * daylight, 0.55f + 0.45f * daylight);
        }
        else
        {
            _sun.RotationDegrees = new Vector3(-48, 215, 0);
            _sun.LightEnergy = 0.07f * Mathf.Clamp(night * 1.2f - 0.2f, 0, 1);
            _sun.LightColor = Moonlight;
        }
        _sun.ShadowBlur = daylight > 0.12f ? 1f : 2.5f;

        _environment.AmbientLightEnergy = 0.05f + 0.25f * daylight;
        _environment.GlowIntensity = 0.2f + 0.5f * night; // lamps and windows bloom at night, not the sunlit streets
        _environment.AmbientLightColor = NightAmbient.Lerp(DayAmbient, daylight);
        _environment.BackgroundEnergyMultiplier = 0.25f + 0.75f * daylight;
        _environment.FogLightColor = NightFog.Lerp(DayFog, daylight);
        _environment.FogDensity = 0.0015f + 0.011f * night;
        if (_environment.Sky?.SkyMaterial is ProceduralSkyMaterial sky)
        {
            sky.SkyTopColor = NightSky.Lerp(DaySky, daylight);
            sky.SkyHorizonColor = NightHorizon.Lerp(DayHorizon, daylight);
            sky.GroundHorizonColor = NightHorizon.Lerp(DayHorizon, daylight);
            sky.GroundBottomColor = NightSky.Lerp(new Color(0.2f, 0.17f, 0.13f), daylight);
            sky.SunAngleMax = daylight > 0.12f ? 30 : 8; // the moon's disc is small
        }
        if (GetParent()?.GetNodeOrNull<Node3D>("Lamps") is { } lamps) lamps.Visible = CityLighting.LampsOn(hour);
    }
}
