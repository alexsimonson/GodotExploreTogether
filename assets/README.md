# Assets

All textures come from the original Unity project's dungeon art (the image assets were made by a friend; the dungeon prefabs are the project owner's own), at
`_reference/ExploreTogether/Assets/Resources/Prefabs/Dungeon`. Only use art from there.
The folder layout and file names under `textures/dungeon/` match the original.
The exceptions are `rockbottom.png` and `roof_wood.png`, converted from `.gif` because Godot can't import GIFs.

**Exception to the "Dungeon folder only" rule:** the torch (`models/torch/`: `torch.fbx`, `torch.png`, `torch_puff.png`)
comes from `Assets/Resources/Models/Torch`. It's included because the original dungeon prefabs (T and X junctions)
use it via the nested "Torch Particle" prefab.

**Item icons** (`icons/`) come from `Assets/Resources/UI`, for the same reason: the original item assets reference them.

## Dungeon pieces (`dungeon_pieces/`, `scenes/dungeon/`)
Generated from the Unity prefabs by `tools/import_unity_dungeon.py` (rerunnable; don't hand-edit the outputs):
exact ProBuilder geometry and UVs (OBJ per piece and material), the original materials (walls brick, floor/ceiling
hardwood, dead-end floor chess, pillars rock), collision (convex, or concave for L-shaped walls), and torch placements.
Look changes made in Godot (`MATERIAL_OVERRIDES` in the script): straight halls keep brick walls; every other piece's walls are rock.
The torch scene (`scenes/props/Torch.tscn`) is hand-built from the Unity "Torch Particle" prefab: the same light color/range,
and fire particles with the original lifetime, speed, cone, 2x2 sheet and color-over-lifetime gradient.
The emission radius is reduced (Unity's 1 m cone base would spill into the walls). Flicker was added.

## Materials (`materials/`)
Converted from the Unity materials: roughness = 1 − Unity smoothness. Godot's height map scale isn't the same unit as Unity's
"parallax", so `heightmap_scale` ≈ parallax × 125 is an estimate to tune by eye.

| Material | Unity source | Maps | Used for |
|---|---|---|---|
| `brick` | `Materials/completethewall.mat` | color, normal, height | Dungeon walls (every piece) |
| `hardwood` | `Materials/hardwood.mat` | color, normal (0.5), height | Dungeon floor/ceiling (every piece) |
| `chess` | `Materials/chess.mat` | color, normal (0.5), metallic | C-Junction (dead end) floor |
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
