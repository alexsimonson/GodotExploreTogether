using System.Collections.Generic;
using System.Linq;
using ExploreTogether.Core.City;
using Godot;

namespace ExploreTogether.City;

/// <summary>
/// The building designs this build of the game knows: the greybox ones plus every scene in
/// <see cref="Folder"/> whose root has the <see cref="CityBuilding"/> script. Loaded once, the same way on
/// server and client; the city's checksum catches a server and client with different scenes.
/// </summary>
public static class CityCatalog
{
    public const string Folder = "res://scenes/city/buildings";

    private static BuildingCatalog? _catalog;
    private static readonly Dictionary<string, PackedScene> Scenes = new();

    public static BuildingCatalog Catalog => _catalog ??= Load();

    /// <summary>The scene for a custom design, or null for a greybox one.</summary>
    public static PackedScene? SceneFor(BuildingVariant variant) => Scenes.GetValueOrDefault(variant.Name);

    /// <summary>Logs which parcels the design can go on, and warns if it never will.</summary>
    private static void ReportFit(string name, BuildingVariant variant)
    {
        var fits = CityLayout.FitsOnLots(variant);
        var used = CityLayout.LotSizesFor(variant.Type);
        var usable = fits.Intersect(used).ToList();
        string Lots(IEnumerable<int> sizes) => string.Join(" and ", sizes.Select(n => $"{n}-lot"));
        if (fits.Count == 0)
            GD.PushWarning($"[city] building '{name}' never fits a parcel: max depth is 9.5 m, max width 10 / 22 / 34 m " +
                           "(1 / 2 / 3 lots), and the door must line up with the gate (see docs/CITY-BUILDINGS.md)");
        else if (usable.Count == 0)
            GD.PushWarning($"[city] building '{name}' fits {Lots(fits)} parcels, but {variant.Type} buildings only go on " +
                           $"{Lots(used)} parcels, so it's never picked (only with --city-favor)");
        else
            GD.Print($"[city]   fits {Lots(fits)} parcels; used on {Lots(usable)} ({variant.Type} goes on {Lots(used)})");
    }

    private static BuildingCatalog Load()
    {
        var custom = new List<BuildingVariant>();
        using var dir = DirAccess.Open(Folder);
        if (dir != null)
        {
            // Exported builds list "X.tscn.remap" instead of "X.tscn"; loading by the .tscn name works in both.
            var files = dir.GetFiles().Select(f => f.EndsWith(".remap") ? f[..^".remap".Length] : f)
                .Where(f => f.EndsWith(".tscn") || f.EndsWith(".scn")).Distinct().OrderBy(f => f);
            foreach (var file in files)
            {
                var path = $"{Folder}/{file}";
                var scene = GD.Load<PackedScene>(path);
                var root = scene?.Instantiate();
                if (root is not CityBuilding building)
                {
                    GD.PushWarning($"[city] {path}: the root node needs the CityBuilding script; skipped");
                    root?.Free();
                    continue;
                }
                var name = file[..file.LastIndexOf('.')];
                var variant = building.ToVariant(name);
                building.Free();
                custom.Add(variant);
                Scenes[name] = scene!;
                GD.Print($"[city] building '{name}': {variant.Type}, {variant.Width} x {variant.Depth} m, " +
                         $"{variant.Floors} floor(s), door at {variant.DoorOffset}, weight {variant.Weight}, {variant.Spots.Count} spot(s)");
                ReportFit(name, variant);
            }
        }
        return BuildingCatalog.WithCustom(custom);
    }
}
