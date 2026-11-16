namespace ExploreTogether.Core.City;

/// <summary>A city's game clock: hours since midnight of day 0, moving at a fixed rate.</summary>
public sealed class CityClock(float startHour, float hoursPerSecond)
{
    /// <summary>Total game hours (keeps growing past 24; see <see cref="HourOfDay"/>).</summary>
    public double Time { get; private set; } = startHour;
    public float HoursPerSecond { get; } = hoursPerSecond;
    public float HourOfDay => (float)(Time % 24);

    public void Advance(double seconds) => Time += seconds * HoursPerSecond;

    /// <summary>"07:45".</summary>
    public static string Format(float hourOfDay)
    {
        var minutes = (int)(hourOfDay * 60) % (24 * 60);
        return $"{minutes / 60:00}:{minutes % 60:00}";
    }
}

/// <summary>
/// One city person's daily life: decides where they should be by the clock, and walks there along streets.
/// Pure logic (positions on the ground plane), run by the server and unit-tested.
///
/// Days (each person shifts their times by up to ±45 minutes):
/// - Civilian: home until 8, errands (shops and walks) until 12, home for lunch, errands 13 to 18, home.
/// - Worker: home until 7, at work 7 to 17, errands until 18:30, home.
/// - Guard: patrols streets near the guardhouse on shift (day 6 to 18, or night), rests in the guardhouse off shift.
///
/// Yaw follows the game's convention: a character faces -Z at yaw 0, so walking along (dx, dz) is atan2(-dx, -dz).
/// </summary>
public sealed class CityNpcBrain
{
    public Person Person { get; }
    public float X { get; private set; }
    public float Z { get; private set; }
    public float Yaw { get; private set; }
    public bool Moving { get; private set; }
    /// <summary>The parcel they're in (once arrived), or -1 on the street or while walking out.</summary>
    public int Inside { get; private set; } = -1;
    /// <summary>What they're doing, for logs and name tags ("working", "shopping", ...).</summary>
    public string Activity { get; private set; } = "";
    public float Speed { get; }
    /// <summary>Killed: stands (lies) still from now on.</summary>
    public bool Dead { get; private set; }
    /// <summary>
    /// Whether they carry a key to a parcel's door (parcel id). The server checks the resident's real inventory;
    /// by default (tests) they have keys to their home and workplace.
    /// </summary>
    public Func<int, bool>? HasKeyTo { get; set; }

    private readonly CityLayout _city;
    private readonly CityPaths _paths;
    private readonly Random _random;
    private readonly (float X, float Z) _lane;
    private readonly float _offset; // personal schedule shift, hours

    private enum GoalKind { Stay, Errands, Patrol }
    private readonly record struct Goal(GoalKind Kind, int Parcel, string Label);

    private Goal _goal;
    private bool _hasGoal;
    /// <summary>A point to walk to; <see cref="Door"/> >= 0 means the leg to it passes through that parcel's door.</summary>
    private readonly record struct Waypoint(float X, float Z, int Door = -1, bool Entering = false)
    {
        public static implicit operator Waypoint((float X, float Z) p) => new(p.X, p.Z);
        public void Deconstruct(out float x, out float z) => (x, z) = (X, Z);
    }

    private readonly Queue<Waypoint> _route = new();
    private CityDoors? _doors;      // this step's doors (from the crowd), if the city has any
    private CityCrowd? _crowd;
    private bool _doorReady;        // the door on the leg being walked has been opened
    private bool _relock;           // they unlocked it from inside to get out: lock it again behind them
    private int _routeEndsIn = -1;     // parcel the current route ends inside, or -1
    private string _routeLabel = "";   // activity once the route is done
    private double _dwellUntil;        // game time when the current stop is over
    private double _dwellHours;        // how long to stay once the current route arrives

    public CityNpcBrain(Person person, CityLayout city, CityPaths paths, int seed, double time)
    {
        Person = person;
        _city = city;
        _paths = paths;
        _random = new Random(unchecked(seed * 31 + person.Id * 7919));
        _lane = ((float)(_random.NextDouble() * 2 - 1) * CityPaths.MaxLaneOffset,
                 (float)(_random.NextDouble() * 2 - 1) * CityPaths.MaxLaneOffset);
        _offset = (float)(_random.NextDouble() * 1.5 - 0.75);
        Speed = person.Role switch { NpcRole.Guard => 1.5f, NpcRole.Worker => 1.4f, _ => 1.25f };
        PlaceAtStart(time);
    }

    /// <summary>Advances by <paramref name="seconds"/> of real time, at game time <paramref name="time"/>.</summary>
    /// <param name="crowd">The others to keep out of (see <see cref="CityCrowd"/>); null walks through everyone.</param>
    public void Update(float seconds, double time, CityCrowd? crowd = null)
    {
        if (Dead) return;
        (_crowd, _doors) = (crowd, crowd?.Doors);
        var goal = DesiredGoal((float)(time % 24));
        if (!_hasGoal || goal != _goal)
        {
            _goal = goal;
            _hasGoal = true;
            StartGoal(time);
        }
        else if (_route.Count == 0 && time >= _dwellUntil)
        {
            NextStep(time);
        }
        Walk(seconds, time, crowd);
    }

    // ---- schedule ----

    private Goal DesiredGoal(float hour)
    {
        var p = Person;
        var h = hour - _offset; // shifting the clock shifts every time in the day
        if (h < 0) h += 24;
        if (h >= 24) h -= 24;
        switch (p.Role)
        {
            case NpcRole.Guard:
                var dayShift = h >= 6 && h < 18;
                return dayShift == (p.Shift == 0)
                    ? new Goal(GoalKind.Patrol, -1, "patrolling")
                    : new Goal(GoalKind.Stay, p.Home, h < 6 || h >= 22 ? "sleeping" : "resting");
            case NpcRole.Worker:
                if (h < 7) return new Goal(GoalKind.Stay, p.Home, h < 6 ? "sleeping" : "at home");
                if (h < 17) return new Goal(GoalKind.Stay, p.Work, "working");
                if (h < 18.5f) return new Goal(GoalKind.Errands, -1, "out");
                return new Goal(GoalKind.Stay, p.Home, h >= 22 ? "sleeping" : "at home");
            default:
                if (h < 8) return new Goal(GoalKind.Stay, p.Home, h < 7 ? "sleeping" : "at home");
                if (h < 12) return new Goal(GoalKind.Errands, -1, "out");
                if (h < 13) return new Goal(GoalKind.Stay, p.Home, "lunch at home");
                if (h < 18) return new Goal(GoalKind.Errands, -1, "out");
                return new Goal(GoalKind.Stay, p.Home, h >= 22 ? "sleeping" : "at home");
        }
    }

    private void PlaceAtStart(double time)
    {
        var goal = DesiredGoal((float)(time % 24));
        if (goal.Kind == GoalKind.Stay && goal.Parcel >= 0)
        {
            var parcel = _city.Parcels[goal.Parcel];
            (X, Z) = RandomSpot(parcel);
            Inside = parcel.Id;
            Activity = goal.Label;
        }
        else
        {
            var cell = goal.Kind == GoalKind.Patrol ? PatrolCell() : RandomStreetCell();
            var (x, z) = CityLayout.CellCenter(cell);
            (X, Z) = (x + _lane.X, z + _lane.Z);
            Activity = goal.Label == "out" ? "walking" : goal.Label;
        }
        _goal = goal;
        _hasGoal = true;
        _dwellUntil = time + _random.NextDouble() * 0.3;
        Yaw = (float)(_random.NextDouble() * Math.PI * 2);
    }

    private void StartGoal(double time)
    {
        switch (_goal.Kind)
        {
            case GoalKind.Stay:
                var parcel = _city.Parcels[_goal.Parcel];
                // Bedtime at home: lock up (from inside, no key needed).
                if (_goal.Label == "sleeping" && Inside == parcel.Id && _doors != null && !_doors.IsOpen(parcel.Id))
                    _doors.Lock(parcel.Id);
                RouteTo(parcel, RandomSpot(parcel), _goal.Label, 0.5 + _random.NextDouble());
                break;
            default:
                NextStep(time);
                break;
        }
    }

    /// <summary>The goal stays the same but the current stop is over: pick the next one.</summary>
    private void NextStep(double time)
    {
        switch (_goal.Kind)
        {
            case GoalKind.Stay:
                // Move to another spot in the same building now and then (not while asleep).
                if (_goal.Label != "sleeping" && Inside == _goal.Parcel || Inside != _goal.Parcel) // or try again to get in
                    RouteTo(_city.Parcels[_goal.Parcel], RandomSpot(_city.Parcels[_goal.Parcel]), _goal.Label, 0.5 + _random.NextDouble());
                else
                    _dwellUntil = time + 0.5 + _random.NextDouble();
                break;
            case GoalKind.Errands:
                if (_random.Next(100) < 45 && NearbyShop() is { } shop)
                    RouteTo(shop, RandomSpot(shop), "shopping", 0.25 + _random.NextDouble() * 0.4);
                else
                    RouteToStreet(RandomStreetCell(near: HereStreetCell(), radius: 8), "walking", 0.05 + _random.NextDouble() * 0.15);
                break;
            case GoalKind.Patrol:
                RouteToStreet(PatrolCell(), "patrolling", 0.03 + _random.NextDouble() * 0.1);
                break;
        }
    }

    // ---- routes ----

    /// <param name="dwellHours">How long to stay once there (game hours, counted from arrival).</param>
    private void RouteTo(Parcel target, (float X, float Z) spot, string label, double dwellHours)
    {
        _dwellHours = dwellHours;
        _route.Clear();
        if (Inside == target.Id)
        {
            _route.Enqueue(spot); // already inside: just cross the room
        }
        else
        {
            var start = LeaveBuilding();
            foreach (var p in _paths.StreetWalk(start, CityPaths.StreetOf(target), _lane)) _route.Enqueue(p);
            var way = CityPaths.WayIn(target, spot);
            for (var i = 0; i < way.Count; i++)
                _route.Enqueue(i == 2 ? new Waypoint(way[i].X, way[i].Z, target.Id, Entering: true) : way[i]); // yard -> just inside
        }
        _routeEndsIn = target.Id;
        _routeLabel = label;
        Activity = Inside == target.Id ? label : "walking";
    }

    private void RouteToStreet(Cell target, string label, double dwellHours)
    {
        _dwellHours = dwellHours;
        _route.Clear();
        var start = LeaveBuilding();
        foreach (var p in _paths.StreetWalk(start, target, _lane)) _route.Enqueue(p);
        _routeEndsIn = -1;
        _routeLabel = label;
        Activity = label == "patrolling" ? label : "walking";
    }

    /// <summary>Queues the way out of the current building (if inside one) and returns the street cell to start from.</summary>
    private Cell LeaveBuilding()
    {
        if (Inside >= 0)
        {
            var parcel = _city.Parcels[Inside];
            var way = CityPaths.WayOut(parcel, (X, Z));
            for (var i = 1; i < way.Count; i++)
                _route.Enqueue(i == 2 ? new Waypoint(way[i].X, way[i].Z, parcel.Id) : way[i]); // just inside -> yard
            Inside = -1;
            return CityPaths.StreetOf(parcel);
        }
        return CurrentStreetCell();
    }

    private float _blockedFor; // seconds the current walk has been held up

    /// <summary>Sidestep directions tried on the street, in degrees from straight ahead (right first).</summary>
    private static readonly float[] Sidesteps = [40, -40, 75, -75, 110, -110];

    private void Walk(float seconds, double time, CityCrowd? crowd)
    {
        var step = Speed * seconds;
        Moving = false;
        if (_route.Count == 0)
        {
            _blockedFor = 0;
            if (crowd != null) MakeRoom(seconds, crowd);
            return;
        }
        while (step > 1e-5f && _route.Count > 0)
        {
            var next = _route.Peek();
            if (next.Door >= 0 && _doors != null && !_doorReady)
            {
                if (!OpenDoor(next))
                {
                    TurnBack(next, time);
                    break;
                }
                _doorReady = true;
            }
            var (tx, tz) = next;
            float dx = tx - X, dz = tz - Z;
            var dist = MathF.Sqrt(dx * dx + dz * dz);
            if (dist <= 1e-4f)
            {
                ReachWaypoint(time);
                continue;
            }
            Yaw = MathF.Atan2(-dx, -dz);
            var move = MathF.Min(step, dist);
            float nx = X + dx / dist * move, nz = Z + dz / dist * move;
            var blocker = crowd?.Check(this, X, Z, nx, nz) ?? CityCrowd.Blocker.None;
            if (blocker == CityCrowd.Blocker.None)
            {
                (X, Z) = (nx, nz);
                step -= move;
                Moving = true;
                _blockedFor = 0;
                if (move >= dist) ReachWaypoint(time);
                continue;
            }

            // Held up.
            _blockedFor += seconds;
            if (_route.Count == 1 && dist < 1.0f)
            {
                // Someone's at the spot: stop here, beside them.
                _route.Clear();
                Arrive(time);
                break;
            }
            if (CityPaths.IsOpenStreet(_city, X, Z) && TrySidestep(crowd!, dx / dist, dz / dist, move))
                break;
            if (blocker == CityCrowd.Blocker.Npc && _blockedFor > CityCrowd.GhostAfterSeconds
                && crowd!.Check(this, X, Z, nx, nz, ignoreNpcs: true) == CityCrowd.Blocker.None)
            {
                // Squeeze past the other resident (only ever along the planned line, so never anywhere new).
                (X, Z) = (nx, nz);
                Moving = true;
                if (move >= dist) ReachWaypoint(time);
            }
            break;
        }
    }

    /// <summary>On the street: step around whoever is in the way, onto open street only.</summary>
    private bool TrySidestep(CityCrowd crowd, float ux, float uz, float move)
    {
        foreach (var degrees in Sidesteps)
        {
            // Positive angles turn right: with +Y up and yaw 0 facing -Z, right of (ux, uz) is (-uz, ux).
            var a = degrees * MathF.PI / 180f;
            float rx = ux * MathF.Cos(a) - uz * MathF.Sin(a), rz = ux * MathF.Sin(a) + uz * MathF.Cos(a);
            float nx = X + rx * move, nz = Z + rz * move;
            if (!CityPaths.IsOpenStreet(_city, nx, nz) || crowd.Check(this, X, Z, nx, nz) != CityCrowd.Blocker.None) continue;
            (X, Z) = (nx, nz);
            Yaw = MathF.Atan2(-rx, -rz);
            Moving = true;
            return true;
        }
        return false;
    }

    /// <summary>Standing still on top of another resident (e.g. both arrived at one spot): edge apart.</summary>
    private void MakeRoom(float seconds, CityCrowd crowd)
    {
        if (crowd.Overlapping(this, X, Z) is not { } other) return;
        float dx = X - other.X, dz = Z - other.Z;
        var d = MathF.Sqrt(dx * dx + dz * dz);
        if (d < 1e-3f) (dx, dz, d) = (Person.Id > other.Person.Id ? 1 : -1, 0, 1); // exactly on top: split along X
        var push = MathF.Min(0.6f * seconds, CityCrowd.NpcRadius * 2 - d);
        float nx = X + dx / d * push, nz = Z + dz / d * push;
        // Only where they can certainly stand: open street, or well inside their own building.
        var ok = Inside >= 0 ? InsideRoom(_city.Parcels[Inside], nx, nz) : CityPaths.IsOpenStreet(_city, nx, nz);
        if (ok && crowd.Check(this, X, Z, nx, nz) == CityCrowd.Blocker.None) (X, Z) = (nx, nz);
    }

    private static bool InsideRoom(Parcel p, float x, float z)
    {
        const float m = 0.6f; // clear of the walls
        var f = p.Footprint;
        return x > f.MinX + m && x < f.MaxX - m && z > f.MinZ + m && z < f.MaxZ - m;
    }

    private void ReachWaypoint(double time)
    {
        var reached = _route.Dequeue();
        if (reached.Door >= 0 && _doors != null) CloseBehind(reached, time);
        _doorReady = false;
        if (_route.Count == 0) Arrive(time);
    }

    // ---- doors ----

    private bool HasKey(int parcel) =>
        HasKeyTo?.Invoke(parcel) ?? (parcel == Person.Home || parcel == Person.Work);

    /// <summary>Opens the door on the way (unlocking it with their key, or from inside). False if they can't.</summary>
    private bool OpenDoor(Waypoint through)
    {
        var door = through.Door;
        if (_doors!.IsOpen(door)) return true;
        _relock = false;
        if (_doors.IsLocked(door))
        {
            if (through.Entering && !HasKey(door)) return false; // locked out
            _relock = !through.Entering; // let themselves out: the latch locks again behind them
            _doors.Unlock(door);
        }
        return _doors.Open(door);
    }

    /// <summary>Through the door: close it (unless a player stands in the way), and lock up when appropriate.</summary>
    private void CloseBehind(Waypoint through, double time)
    {
        var door = through.Door;
        var p = _city.Parcels[door];
        if (_crowd?.PlayerNear(p.Door.X, p.Door.Z, 1.6f) == true) return; // leave it open rather than shut it on someone
        _doors!.Close(door);
        var hour = time % 24;
        bool lockIt;
        if (through.Entering)
            lockIt = door == Person.Home && (hour >= 21 || hour < 6); // home for the night
        else
            lockIt = _relock || HasKey(door) && _crowd?.AnyoneBelongingInside(door, this) != true; // last one out
        if (lockIt) _doors.Lock(door);
    }

    /// <summary>Locked out (no key): walk back out through the gate to the street, and do something else.</summary>
    private void TurnBack(Waypoint through, double time)
    {
        var p = _city.Parcels[through.Door];
        _route.Clear();
        _route.Enqueue(CityPaths.WayIn(p, p.Spots[0])[0]); // the street in front of the gate
        _routeEndsIn = -1;
        _routeLabel = "walking";
        _dwellHours = 0.02;
        _doorReady = false;
        Activity = "walking";
    }

    /// <summary>Killed: drops where they stand and does nothing more.</summary>
    public void Die()
    {
        Dead = true;
        Moving = false;
        _route.Clear();
        Activity = "dead";
    }

    private void Arrive(double time)
    {
        Inside = _routeEndsIn;
        Activity = _routeLabel;
        _dwellUntil = time + _dwellHours;
    }

    // ---- places ----

    private (float X, float Z) RandomSpot(Parcel p) => p.Spots[_random.Next(p.Spots.Count)];

    /// <summary>The street cell they're on, or the one in front of the building they're in.</summary>
    private Cell HereStreetCell() => Inside >= 0 ? CityPaths.StreetOf(_city.Parcels[Inside]) : CurrentStreetCell();

    private Cell CurrentStreetCell()
    {
        var c = CityLayout.CellAt(X - _lane.X, Z - _lane.Z);
        if (_city.IsStreet(c)) return c;
        c = CityLayout.CellAt(X, Z);
        if (_city.IsStreet(c)) return c;
        // Shouldn't happen (people are only ever on streets or in their own buildings): use the nearest street.
        var (x, z) = (X, Z);
        return _paths.StreetCells.MinBy(s =>
        {
            var (sx, sz) = CityLayout.CellCenter(s);
            return (sx - x) * (sx - x) + (sz - z) * (sz - z);
        });
    }

    private Cell RandomStreetCell(Cell? near = null, int radius = int.MaxValue)
    {
        var cells = near is { } n
            ? _paths.StreetCells.Where(c => Math.Abs(c.X - n.X) + Math.Abs(c.Z - n.Z) <= radius).ToList()
            : _paths.StreetCells;
        return cells[_random.Next(cells.Count)];
    }

    /// <summary>A street cell within 8 cells (about 100 m) of the guard's post.</summary>
    private Cell PatrolCell() =>
        RandomStreetCell(near: CityPaths.StreetOf(_city.Parcels[Person.Work]), radius: 8);

    /// <summary>One of the five shops nearest to where they are, or null if the city has none.</summary>
    private Parcel? NearbyShop()
    {
        var (x, z) = (X, Z);
        var shops = _city.Parcels.Where(p => p.IsPublic && (_doors == null || !_doors.IsLocked(p.Id)))
            .OrderBy(p => (p.Door.X - x) * (p.Door.X - x) + (p.Door.Z - z) * (p.Door.Z - z))
            .Take(5).ToList();
        return shops.Count == 0 ? null : shops[_random.Next(shops.Count)];
    }
}
