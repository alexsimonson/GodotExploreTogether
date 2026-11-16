using System.Linq;
using ExploreTogether.City;
using ExploreTogether.Core.City;
using ExploreTogether.Net;
using Godot;

namespace ExploreTogether.Client;

/// <summary>
/// Client side of the city: the clock in the top-right corner, and daylight that follows it.
/// The server sends the clock on arrival and every few seconds; in between, the client runs it on at the same rate.
/// </summary>
public partial class ClientGame
{
    private float _cityHour = -1, _cityHoursPerSecond;

    private void OnCityClock(float hour, float hoursPerSecond)
    {
        _cityHour = hour;
        _cityHoursPerSecond = hoursPerSecond;
    }

    private void CityProcess(double delta)
    {
        if (_currentKind != InstanceKinds.City || _cityHour < 0) return;
        _cityHour = (_cityHour + (float)delta * _cityHoursPerSecond) % 24f;
        _statusLabel.Text = $"City time {CityClock.Format(_cityHour)}";
        _statusLabel.Visible = true;
        if (_currentInstanceId is { } id
            && _main.Instances.GetNodeOrNull<CityDaylight>($"{InstanceFactory.NodeName(id)}/Level/Daylight") is { } daylight)
            daylight.SetHour(_cityHour);
    }

    /// <summary>--dev-check-city: report the residents this client can see, and how many are walking.</summary>
    private void DevReportNpcs(int instanceId)
    {
        var npcs = _main.Instances.GetNodeOrNull(InstanceFactory.NodeName(instanceId))?.GetNode("Npcs").GetChildren()
            .OfType<CityNpc>().ToList() ?? [];
        var roles = string.Join(", ", npcs.GroupBy(n => n.Role).Select(g => $"{g.Count()} {g.Key}"));
        GD.Print($"[client] city: {npcs.Count} residents in view ({roles}), {npcs.Count(n => n.NetMoving)} walking, " +
                 $"clock {(_cityHour < 0 ? "not received" : CityClock.Format(_cityHour))}");
    }
}
