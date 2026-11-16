using ExploreTogether.Core.Items;

namespace ExploreTogether.Core.City;

/// <summary>
/// The front doors of a city's buildings (one per parcel): open or closed, locked or unlocked.
/// The server owns this state; residents and players change it, and clients are told.
///
/// Rules:
/// - A locked door can't be opened. Locking needs the door closed.
/// - From outside, locking and unlocking need a key for that door (an item whose data is the lock's code).
///   From inside, anyone can (a thumb-turn): nobody gets locked in.
/// - Doors swing one way only, into the building (that's presentation; here a door is just open or closed).
/// </summary>
public sealed class CityDoors
{
    public readonly record struct DoorState(bool Open, bool Locked);

    private readonly DoorState[] _doors;
    private readonly int _codeBase;

    /// <summary>A door changed (parcel id).</summary>
    public event Action<int>? Changed;

    public CityDoors(CityLayout city)
    {
        _doors = new DoorState[city.Parcels.Count];
        // The seed picks the high bits, the parcel id the low 12: unique within a city, different between cities.
        _codeBase = (int)((uint)unchecked(city.Seed * 2654435761u) >> 12 << 12) & 0x7fffffff;
    }

    public int Count => _doors.Length;
    public DoorState this[int parcel] => _doors[parcel];
    public bool IsOpen(int parcel) => _doors[parcel].Open;
    public bool IsLocked(int parcel) => _doors[parcel].Locked;

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

    public void Close(int parcel)
    {
        if (_doors[parcel].Open) Set(parcel, _doors[parcel] with { Open = false });
    }

    /// <summary>Locks a closed door (an open one can't be locked). The caller checks the key (or that they're inside).</summary>
    public bool Lock(int parcel)
    {
        var d = _doors[parcel];
        if (d.Open) return false;
        if (!d.Locked) Set(parcel, d with { Locked = true });
        return true;
    }

    public void Unlock(int parcel)
    {
        if (_doors[parcel].Locked) Set(parcel, _doors[parcel] with { Locked = false });
    }

    /// <summary>Every door as (parcel, open, locked) triples, for a client arriving in the city.</summary>
    public int[] Export()
    {
        var data = new int[_doors.Length * 3];
        for (var i = 0; i < _doors.Length; i++)
            (data[i * 3], data[i * 3 + 1], data[i * 3 + 2]) = (i, _doors[i].Open ? 1 : 0, _doors[i].Locked ? 1 : 0);
        return data;
    }

    private void Set(int parcel, DoorState state)
    {
        _doors[parcel] = state;
        Changed?.Invoke(parcel);
    }
}
