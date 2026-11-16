using ExploreTogether.Core.Items;

namespace ExploreTogether.Core.City;

/// <summary>
/// The front doors of a city's buildings (one per parcel): open or closed, locked or unlocked, and the damage
/// done breaking in. The server owns this state; residents and players change it, and clients are told.
///
/// Rules:
/// - A locked door can't be opened. Locking needs the door closed and the lock intact.
/// - From outside, locking and unlocking need a key for that door (an item whose data is the lock's code).
///   From inside, anyone can (a thumb-turn): nobody gets locked in.
/// - Breaking in (see <see cref="Breaching"/>):
///   - <see cref="DoorState.LockBroken"/>: a slug through the deadbolt. The door is unlocked for good; it still
///     opens and closes.
///   - <see cref="DoorState.Broken"/>: kicked in. Forced open, hanging off its hinges: it can't be closed or locked.
///   Both stay until the city closes (nobody repairs them yet), and residents notice them.
/// - Doors swing one way only, into the building (that's presentation; here a door is just open or closed).
/// - <see cref="DoorState.Lit"/> isn't the door at all: whether the building's lights are on, sent along with it.
/// </summary>
public sealed class CityDoors
{
    public readonly record struct DoorState(bool Open, bool Locked, bool Broken = false, bool LockBroken = false, bool Lit = false)
    {
        /// <summary>Visible damage: a kicked-in door or a shot-out lock.</summary>
        public bool Damaged => Broken || LockBroken;

        public int Flags => (Open ? 1 : 0) | (Locked ? 2 : 0) | (Broken ? 4 : 0) | (LockBroken ? 8 : 0) | (Lit ? 16 : 0);

        public static DoorState FromFlags(int f) =>
            new((f & 1) != 0, (f & 2) != 0, (f & 4) != 0, (f & 8) != 0, (f & 16) != 0);
    }

    private readonly DoorState[] _doors;
    private readonly int[] _kicks;
    private readonly int _codeBase;

    /// <summary>A door changed (parcel id).</summary>
    public event Action<int>? Changed;

    public CityDoors(CityLayout city)
    {
        _doors = new DoorState[city.Parcels.Count];
        _kicks = new int[city.Parcels.Count];
        // The seed picks the high bits, the parcel id the low 12: unique within a city, different between cities.
        _codeBase = (int)((uint)unchecked(city.Seed * 2654435761u) >> 12 << 12) & 0x7fffffff;
    }

    public int Count => _doors.Length;
    public DoorState this[int parcel] => _doors[parcel];
    public bool IsOpen(int parcel) => _doors[parcel].Open;
    public bool IsLocked(int parcel) => _doors[parcel].Locked;
    public bool IsBroken(int parcel) => _doors[parcel].Broken;
    public bool IsDamaged(int parcel) => _doors[parcel].Damaged;
    /// <summary>Kicks taken so far (see <see cref="Breaching.KicksToBreak"/>).</summary>
    public int Kicks(int parcel) => _kicks[parcel];

    /// <summary>The code a key needs (as its item data) to fit this parcel's door.</summary>
    public int LockCode(int parcel) => _codeBase | parcel;

    /// <summary>The parcel whose door a key fits, or -1.</summary>
    public int ParcelForKey(int keyData) =>
        (keyData & ~0xfff) == _codeBase && (keyData & 0xfff) < _doors.Length ? keyData & 0xfff : -1;

    /// <summary>Does this inventory hold a key to the parcel's door?</summary>
    public bool HasKey(Inventory inventory, int parcel)
    {
        var code = LockCode(parcel);
        return inventory.slots.Any(s => s.itemId == ItemCatalog.KeyId && s.data == code);
    }

    public bool Open(int parcel)
    {
        var d = _doors[parcel];
        if (d.Open) return true;
        if (d.Locked) return false;
        Set(parcel, d with { Open = true });
        return true;
    }

    /// <summary>Closes the door (a broken one can't be closed).</summary>
    public void Close(int parcel)
    {
        if (_doors[parcel].Open && !_doors[parcel].Broken) Set(parcel, _doors[parcel] with { Open = false });
    }

    /// <summary>Locks a closed door with a working lock. The caller checks the key (or that they're inside).</summary>
    public bool Lock(int parcel)
    {
        var d = _doors[parcel];
        if (d.Open || d.LockBroken || d.Broken) return false;
        if (!d.Locked) Set(parcel, d with { Locked = true });
        return true;
    }

    public void Unlock(int parcel)
    {
        if (_doors[parcel].Locked) Set(parcel, _doors[parcel] with { Locked = false });
    }

    /// <summary>A slug through the deadbolt: unlocked, and it can never be locked again. False if already gone.</summary>
    public bool BreakLock(int parcel)
    {
        var d = _doors[parcel];
        if (d.LockBroken || d.Broken) return false;
        Set(parcel, d with { Locked = false, LockBroken = true });
        return true;
    }

    /// <summary>
    /// A kick at a closed door (an open or broken one takes none): counts toward <paramref name="strength"/>
    /// kicks, or bursts an unlocked door at once. Returns true when this kick breaks it (forced open for good).
    /// </summary>
    public bool Kick(int parcel, int strength)
    {
        var d = _doors[parcel];
        if (d.Open || d.Broken) return false;
        if (!d.Locked || ++_kicks[parcel] >= strength)
        {
            Set(parcel, d with { Open = true, Locked = false, Broken = true });
            return true;
        }
        return false;
    }

    /// <summary>The building's lights (sent with the door, so clients light the windows).</summary>
    public void SetLit(int parcel, bool lit)
    {
        if (_doors[parcel].Lit != lit) Set(parcel, _doors[parcel] with { Lit = lit });
    }

    /// <summary>Every door as (parcel, flags) pairs (see <see cref="DoorState.Flags"/>), for a client arriving in the city.</summary>
    public int[] Export()
    {
        var data = new int[_doors.Length * 2];
        for (var i = 0; i < _doors.Length; i++)
            (data[i * 2], data[i * 2 + 1]) = (i, _doors[i].Flags);
        return data;
    }

    private void Set(int parcel, DoorState state)
    {
        _doors[parcel] = state;
        Changed?.Invoke(parcel);
    }
}
