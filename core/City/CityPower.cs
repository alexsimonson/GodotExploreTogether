namespace ExploreTogether.Core.City;

/// <summary>A building's light switch: left to the residents, or flipped by a player.</summary>
public enum LightSwitch { Auto, Off, On }

/// <summary>
/// A city's electricity and everything that can put its lights out:
///
/// - <b>The grid.</b> The power station (one per city, a building with the main breaker inside) feeds a substation in
///   each district (a fenced cabinet on a street corner; districts are blocks of 3×3 tiles), which feeds every
///   building's fuse box (a metal box on its front wall, by the door) and the district's street lamps. Cut any link
///   and everything below it goes dark: <see cref="Powered"/>, <see cref="LampLit"/>.
/// - <b>Fuse boxes</b> are locked: break one open (hit or shoot it), then flip its breaker (E).
/// - <b>Light switches</b> inside each front door (E). Off, the building stays dark until someone who lives or
///   works there is awake inside and notices (they switch it back on); on, it's lit even with nobody home.
/// - <b>Broken lights</b>: street lamps, porch lights and ceiling lamps can be shot out (or hit, where they're in
///   reach). Broken stays broken.
///
/// Server and client both hold one; the server's is the truth and travels as <see cref="Export"/>.
/// </summary>
public sealed class CityPower
{
    public enum Box { Locked, Open, Cut }

    /// <summary>Tiles per side of a district.</summary>
    public const int DistrictTiles = 3;

    private readonly CityLayout _city;
    private readonly CityLighting _lighting;
    private readonly int[] _parcelDistrict, _lampDistrict;
    private readonly Box[] _boxes;
    private readonly LightSwitch[] _switches;
    private readonly bool[] _ceilingBroken, _lampBroken, _porchBroken, _substationDown;
    private bool _stationDown;

    public event Action? Changed;

    /// <summary>Substations, one per district that has any street: where they stand (a street corner), by district index.</summary>
    public IReadOnlyList<(float X, float Z)?> Substations { get; }
    public int DistrictsX { get; }
    public int Districts => _substationDown.Length;
    /// <summary>The power station's parcel (-1: the city has none, so the grid can't be cut at the top).</summary>
    public int Station { get; }

    public CityPower(CityLayout city, CityLighting lighting)
    {
        (_city, _lighting) = (city, lighting);
        DistrictsX = (city.Width + DistrictTiles - 1) / DistrictTiles;
        var districtsZ = (city.Height + DistrictTiles - 1) / DistrictTiles;
        var count = DistrictsX * districtsZ;
        _parcelDistrict = city.Parcels.Select(p => DistrictAt(p.Door.X, p.Door.Z)).ToArray();
        _lampDistrict = lighting.Lamps.Select(l => DistrictAt(l.X, l.Z)).ToArray();
        _boxes = new Box[city.Parcels.Count];
        _switches = new LightSwitch[city.Parcels.Count];
        _ceilingBroken = new bool[city.Parcels.Count];
        _lampBroken = new bool[lighting.Lamps.Count];
        _porchBroken = new bool[lighting.Porches.Count];
        _substationDown = new bool[count];
        Substations = PlaceSubstations(count);
        Station = city.Parcels.FirstOrDefault(p => p.Building == BuildingType.PowerStation)?.Id ?? -1;
    }

    /// <summary>The district a point is in.</summary>
    public int DistrictAt(float x, float z)
    {
        var c = CityLayout.CellAt(x, z);
        var tx = Math.Clamp(c.X / 2, 0, _city.Width - 1) / DistrictTiles;
        var tz = Math.Clamp(c.Z / 2, 0, _city.Height - 1) / DistrictTiles;
        return tz * DistrictsX + tx;
    }

    public int DistrictOf(int parcel) => _parcelDistrict[parcel];

    /// <summary>
    /// A substation per district: on the street cell nearest the district's middle, in a corner of the cell that no
    /// street lamp uses (lamps stand 5.2 m out on the diagonals; the cabinet stands 4.6 m out).
    /// </summary>
    private List<(float X, float Z)?> PlaceSubstations(int count)
    {
        var list = new List<(float X, float Z)?>();
        for (var d = 0; d < count; d++)
        {
            int dx = d % DistrictsX, dz = d / DistrictsX;
            var tiles = Math.Min(DistrictTiles, _city.Width - dx * DistrictTiles);
            var tilesZ = Math.Min(DistrictTiles, _city.Height - dz * DistrictTiles);
            // District middle, in cells (two cells per tile).
            var mx = (dx * DistrictTiles + tiles / 2f) * 2;
            var mz = (dz * DistrictTiles + tilesZ / 2f) * 2;
            Cell? best = null;
            var bestD = float.MaxValue;
            for (var x = dx * DistrictTiles * 2; x < (dx * DistrictTiles + tiles) * 2; x++)
                for (var z = dz * DistrictTiles * 2; z < (dz * DistrictTiles + tilesZ) * 2; z++)
                {
                    var c = new Cell(x, z);
                    if (!_city.IsStreet(c)) continue;
                    var dist = (x - mx) * (x - mx) + (z - mz) * (z - mz);
                    if (dist < bestD) (best, bestD) = (c, dist);
                }
            if (best is not { } cell)
            {
                list.Add(null);
                continue;
            }
            var (cx, cz) = CityLayout.CellCenter(cell);
            (float X, float Z)? spot = null;
            foreach (var (sx, sz) in new[] { (1, 1), (-1, 1), (1, -1), (-1, -1) })
            {
                var corner = (X: cx + sx * 4.6f, Z: cz + sz * 4.6f);
                if (_lighting.LampPosts.Any(l => MathF.Abs(l.X - corner.X) < 2 && MathF.Abs(l.Z - corner.Z) < 2)) continue;
                spot = corner;
                break;
            }
            list.Add(spot ?? (cx + 4.6f, cz + 4.6f));
        }
        return list;
    }

    // ---- what's lit ----

    /// <summary>Is the building getting electricity (its fuse box, its district's substation and the station all on)?</summary>
    public bool Powered(int parcel) =>
        _boxes[parcel] != Box.Cut && !_substationDown[_parcelDistrict[parcel]] && !_stationDown;

    /// <summary>Can the building's lights come on at all (powered, and the ceiling lamps not broken)?</summary>
    public bool CanLight(int parcel) => Powered(parcel) && !_ceilingBroken[parcel];

    /// <summary>Is the building lit, given whether someone awake inside would have the lights on?</summary>
    public bool BuildingLit(int parcel, bool occupantsWantLight) => CanLight(parcel) && _switches[parcel] switch
    {
        LightSwitch.Off => false,
        LightSwitch.On => true,
        _ => occupantsWantLight,
    };

    /// <summary>Is street lamp <paramref name="lamp"/> burning at that hour?</summary>
    public bool LampLit(int lamp, float hour) =>
        CityLighting.LampsOn(hour) && !_lampBroken[lamp] && !_substationDown[_lampDistrict[lamp]] && !_stationDown;

    /// <summary>Is porch light <paramref name="porch"/> burning at that hour?</summary>
    public bool PorchLit(int porch, float hour) =>
        CityLighting.LampsOn(hour) && !_porchBroken[porch] && Powered(_lighting.Porches[porch].Parcel);

    public Box BoxState(int parcel) => _boxes[parcel];
    public LightSwitch SwitchState(int parcel) => _switches[parcel];
    public bool SubstationDown(int district) => _substationDown[district];
    public bool StationDown => _stationDown;
    public bool LampBroken(int lamp) => _lampBroken[lamp];
    public bool PorchBroken(int porch) => _porchBroken[porch];
    public bool CeilingBroken(int parcel) => _ceilingBroken[parcel];

    // ---- changes (server) ----

    /// <summary>A fuse box hit hard enough: its lock gives. False if it was already open.</summary>
    public bool BreakBoxOpen(int parcel)
    {
        if (_boxes[parcel] != Box.Locked) return false;
        _boxes[parcel] = Box.Open;
        Changed?.Invoke();
        return true;
    }

    /// <summary>The breaker in an open fuse box: cut the building's power, or restore it. False if the box is locked.</summary>
    public bool FlipBreaker(int parcel)
    {
        if (_boxes[parcel] == Box.Locked) return false;
        _boxes[parcel] = _boxes[parcel] == Box.Cut ? Box.Open : Box.Cut;
        Changed?.Invoke();
        return true;
    }

    /// <summary>A light switch: off if the lights are on (or would be), on otherwise.</summary>
    public void FlipSwitch(int parcel, bool currentlyLit)
    {
        _switches[parcel] = currentlyLit ? LightSwitch.Off : LightSwitch.On;
        Changed?.Invoke();
    }

    /// <summary>Someone who belongs there put the lights back to normal (they notice it's dark, or leave).</summary>
    public void ResetSwitch(int parcel)
    {
        if (_switches[parcel] == LightSwitch.Auto) return;
        _switches[parcel] = LightSwitch.Auto;
        Changed?.Invoke();
    }

    public void SetSubstation(int district, bool down)
    {
        if (_substationDown[district] == down) return;
        _substationDown[district] = down;
        Changed?.Invoke();
    }

    public void SetStation(bool down)
    {
        if (_stationDown == down) return;
        _stationDown = down;
        Changed?.Invoke();
    }

    public bool BreakLamp(int lamp) => Break(_lampBroken, lamp);
    public bool BreakPorch(int porch) => Break(_porchBroken, porch);
    public bool BreakCeiling(int parcel) => Break(_ceilingBroken, parcel);

    private bool Break(bool[] list, int index)
    {
        if (index < 0 || index >= list.Length || list[index]) return false;
        list[index] = true;
        Changed?.Invoke();
        return true;
    }

    // ---- the wire format ----

    /// <summary>
    /// Everything as ints: [station, districts..., then per parcel (box | switch &lt;&lt; 2 | ceiling broken &lt;&lt; 4),
    /// then per lamp (broken), then per porch (broken)].
    /// </summary>
    public int[] Export()
    {
        var data = new List<int> { _stationDown ? 1 : 0 };
        data.AddRange(_substationDown.Select(b => b ? 1 : 0));
        for (var p = 0; p < _boxes.Length; p++)
            data.Add((int)_boxes[p] | (int)_switches[p] << 2 | (_ceilingBroken[p] ? 1 : 0) << 4);
        data.AddRange(_lampBroken.Select(b => b ? 1 : 0));
        data.AddRange(_porchBroken.Select(b => b ? 1 : 0));
        return data.ToArray();
    }

    /// <summary>Takes the server's state (ignored if it doesn't match this city's size).</summary>
    public void Import(int[] data)
    {
        if (data.Length != 1 + _substationDown.Length + _boxes.Length + _lampBroken.Length + _porchBroken.Length) return;
        var i = 0;
        _stationDown = data[i++] != 0;
        for (var d = 0; d < _substationDown.Length; d++) _substationDown[d] = data[i++] != 0;
        for (var p = 0; p < _boxes.Length; p++)
        {
            var v = data[i++];
            _boxes[p] = (Box)(v & 3);
            _switches[p] = (LightSwitch)((v >> 2) & 3);
            _ceilingBroken[p] = (v & 16) != 0;
        }
        for (var l = 0; l < _lampBroken.Length; l++) _lampBroken[l] = data[i++] != 0;
        for (var l = 0; l < _porchBroken.Length; l++) _porchBroken[l] = data[i++] != 0;
        Changed?.Invoke();
    }
}
