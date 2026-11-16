using System.Text;
using ExploreTogether.Core.Dungeon;

namespace ExploreTogether.Core.City;

/// <summary>
/// A text map of a city for review and logs, north at the top. Each 12 m cell is 3×3 characters:
/// blank is street, '#' a fence between parcels, the building's letter where it stands
/// (<see cref="Letter"/>), '+' its door, and '.' yard.
/// </summary>
public static class CityMap
{
    public static char Letter(BuildingType type) => type switch
    {
        BuildingType.Cottage => 'c',
        BuildingType.House => 'h',
        BuildingType.RowHouse => 'r',
        BuildingType.Manor => 'M',
        BuildingType.Shop => 'S',
        BuildingType.Office => 'O',
        BuildingType.Warehouse => 'W',
        BuildingType.Guardhouse => 'G',
        _ => '?',
    };

    public static string Render(CityLayout city)
    {
        const int k = 3; // characters per cell
        int w = city.CellsX * k, h = city.CellsZ * k;
        var grid = new char[h, w];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                grid[y, x] = ' ';

        for (var cz = 0; cz < city.CellsZ; cz++)
            for (var cx = 0; cx < city.CellsX; cx++)
            {
                var cell = new Cell(cx, cz);
                var parcel = city.ParcelOf(cell);
                if (parcel == null) continue;
                var rect = CityLayout.CellRect(cell);
                for (var sz = 0; sz < k; sz++)
                    for (var sx = 0; sx < k; sx++)
                    {
                        // Sample the middle of each sub-square of the cell.
                        var px = rect.MinX + (sx + 0.5f) * CityLayout.CellSize / k;
                        var pz = rect.MinZ + (sz + 0.5f) * CityLayout.CellSize / k;
                        var ch = parcel.Footprint.Contains(px, pz) ? Letter(parcel.Building) : '.';
                        // Fence: the sub-square sits on the parcel's edge toward a cell of another parcel or the city edge.
                        foreach (var (side, onEdge) in new[] { (Sides.West, sx == 0), (Sides.East, sx == k - 1), (Sides.South, sz == 0), (Sides.North, sz == k - 1) })
                        {
                            if (!onEdge) continue;
                            var (dx, dz) = MazePieces.Vector(side);
                            var other = new Cell(cx + dx, cz + dz);
                            if (!city.IsStreet(other) && city.ParcelOf(other) != parcel) ch = '#';
                        }
                        grid[h - 1 - (cz * k + sz), cx * k + sx] = ch;
                    }
            }

        foreach (var p in city.Parcels)
        {
            var cell = CityLayout.CellAt(p.Door.X, p.Door.Z);
            var rect = CityLayout.CellRect(cell);
            var sx = Math.Clamp((int)((p.Door.X - rect.MinX) / (CityLayout.CellSize / (float)k)), 0, k - 1);
            var sz = Math.Clamp((int)((p.Door.Z - rect.MinZ) / (CityLayout.CellSize / (float)k)), 0, k - 1);
            grid[h - 1 - (cell.Z * k + sz), cell.X * k + sx] = '+';
        }

        var sb = new StringBuilder();
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++) sb.Append(grid[y, x]);
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
