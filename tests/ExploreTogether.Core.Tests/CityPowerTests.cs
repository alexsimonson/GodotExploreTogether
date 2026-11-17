using ExploreTogether.Core.City;

namespace ExploreTogether.Core.Tests;

public class CityPowerTests
{
    private static (CityLayout City, CityLighting Lighting, CityPower Power) Make(int size = 6, int seed = 3)
    {
        var city = CityLayout.Generate(size, size, seed);
        var lighting = new CityLighting(city);
        var power = new CityPower(city, lighting);
        lighting.Power = power;
        return (city, lighting, power);
    }

    [Fact]
    public void EveryCity_HasAPowerStation_AndASubstationPerDistrict()
    {
        var (city, _, power) = Make();
        Assert.Single(city.Parcels, p => p.Building == BuildingType.PowerStation);
        Assert.True(power.Station >= 0);
        Assert.Equal(4, power.Districts); // 6x6 tiles: 2x2 districts of 3x3
        Assert.All(power.Substations, s => Assert.NotNull(s));
    }

    [Fact]
    public void CuttingAFuseBox_DarkensOnlyThatBuilding()
    {
        var (city, _, power) = Make();
        var home = city.Parcels.First(p => CityPopulation.HomeCapacity(p.Building) > 0);
        Assert.False(power.FlipBreaker(home.Id)); // locked
        Assert.True(power.BreakBoxOpen(home.Id));
        Assert.True(power.FlipBreaker(home.Id));
        Assert.False(power.BuildingLit(home.Id, occupantsWantLight: true));
        Assert.True(city.Parcels.Where(p => p.Id != home.Id).All(p => power.Powered(p.Id)));
        Assert.True(power.FlipBreaker(home.Id)); // and back on
        Assert.True(power.BuildingLit(home.Id, true));
    }

    [Fact]
    public void ASubstation_DarkensItsDistrict_StreetLampsAndAll()
    {
        var (city, lighting, power) = Make();
        var d = power.DistrictOf(city.Parcels[0].Id);
        var lamp = Enumerable.Range(0, lighting.Lamps.Count).First(i => power.DistrictAt(lighting.Lamps[i].X, lighting.Lamps[i].Z) == d);
        Assert.True(power.LampLit(lamp, 23));
        power.SetSubstation(d, true);
        Assert.False(power.LampLit(lamp, 23));
        Assert.False(power.Powered(city.Parcels[0].Id));
        Assert.Contains(city.Parcels, p => power.DistrictOf(p.Id) != d && power.Powered(p.Id));
        power.SetSubstation(d, false);
        power.SetStation(true); // the whole city
        Assert.True(city.Parcels.All(p => !power.Powered(p.Id)));
        Assert.True(Enumerable.Range(0, lighting.Lamps.Count).All(i => !power.LampLit(i, 23)));
    }

    [Fact]
    public void ABrokenLamp_LeavesItsPatchDark()
    {
        var (_, lighting, power) = Make();
        var (x, z) = lighting.Lamps[0];
        var before = lighting.LightAt(x, z, 23);
        power.BreakLamp(0);
        Assert.True(lighting.LightAt(x, z, 23) < before);
        Assert.False(power.BreakLamp(0)); // already broken
    }

    [Fact]
    public void Switches_OverrideTheResidents_UntilReset()
    {
        var (city, _, power) = Make();
        var p = city.Parcels[1].Id;
        power.FlipSwitch(p, currentlyLit: true);
        Assert.False(power.BuildingLit(p, true));
        power.ResetSwitch(p);
        Assert.True(power.BuildingLit(p, true));
        power.FlipSwitch(p, currentlyLit: false);
        Assert.True(power.BuildingLit(p, false)); // on with nobody home
    }

    [Fact]
    public void State_TravelsAsInts()
    {
        var (city, lighting, power) = Make();
        power.BreakBoxOpen(2);
        power.FlipBreaker(2);
        power.SetSubstation(1, true);
        power.BreakPorch(0);
        power.BreakCeiling(5);
        power.FlipSwitch(7, true);
        var copy = new CityPower(city, lighting);
        copy.Import(power.Export());
        Assert.Equal(power.Export(), copy.Export());
        Assert.Equal(CityPower.Box.Cut, copy.BoxState(2));
        Assert.True(copy.SubstationDown(1));
    }
}
