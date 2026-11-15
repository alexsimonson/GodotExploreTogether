# Assets

All textures come from the original Unity project's dungeon art (made by a friend), at
`_reference/ExploreTogether/Assets/Resources/Prefabs/Dungeon`. Only use art from there.
The folder layout and file names under `textures/dungeon/` match the original.
The exceptions are `rockbottom.png` and `roof_wood.png`, converted from `.gif` because Godot can't import GIFs.

## Materials (`materials/`)
Converted from the Unity materials: roughness = 1 − Unity smoothness. Godot's height map scale isn't the same unit as Unity's
"parallax", so `heightmap_scale` ≈ parallax × 125 is an estimate to tune by eye.

| Material | Unity source | Maps | Used for |
|---|---|---|---|
| `brick` | `Materials/completethewall.mat` | color, normal, height | Dungeon walls (every piece) |
| `hardwood` | `Materials/hardwood.mat` | color, normal (0.5), height | Dungeon floor/ceiling (every piece) |
| `chess` | `Materials/chess.mat` | color, normal (0.5), metallic | C-Junction (dead end) |
| `rock` | `grass_rock/Materials/Rock.mat` | color, normal, height | Not used by the Unity pieces |
| `dirt` | `grass_rock/Materials/Dirt_Texture.mat` | color | Not used by the Unity pieces |
| `rockbottom` | `Materials/rockbottom.mat` | color | Not used by the Unity pieces |
| `roof_wood` | `Materials/roof_wood.mat` | color, normal*, height* | Not used by the Unity pieces |

The base materials map one texture repeat per mesh face (one 5 m dungeon cell face, as in Unity).
The `hub_*` materials reuse the same textures, tiled for the hub's large surfaces.

\* Generated here, not from the original art: `roof_wood_height.png` and `roof_wood_normal.png` were made from
`roof_wood.png`'s brightness with `tools/generate_height_map.gd` (blur 1, normal strength 4). Rerun it to adjust:
`<godot>_console.exe --headless --path . --script res://tools/generate_height_map.gd -- <in> <out_height> <out_normal> [blur] [strength] [invert]`

## Import notes
- Normal maps must be imported with `compress/normal_map=1` (set in their `.import` files).
- Height maps don't work with triplanar mapping in Godot, so triplanar materials (`hub_rock_block`) have no height map.
