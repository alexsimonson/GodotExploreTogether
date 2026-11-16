# Making your own city buildings

The city generator places a building on every property (parcel). Out of the box it uses greybox boxes, one design per building type. You can add your own designs as Godot scenes. The generator then mixes them into every new city, alongside the greybox ones.

A working example is in [scenes/city/buildings/ExampleCottage.tscn](../scenes/city/buildings/ExampleCottage.tscn): a brick cottage with an off-center door and a porch.

## Quick start

1. Duplicate `scenes/city/buildings/ExampleCottage.tscn` in the same folder and rename it, e.g. `BakerShop.tscn`. **The file name is the design's name.**
2. Open it. Select the root node and set the properties in the Inspector (see [Properties](#properties)). A green outline shows the footprint you declared, a yellow frame shows the doorway, and a cyan arrow points to the street.
3. Replace the meshes with your building, keeping it inside the green outline, and give it collision (see [Requirements](#requirements)).
4. Try it out (see [Testing a building](#testing-a-building)): `tools\dev-run.cmd 1 --city-favor BakerShop`, then walk through the gold City Gate in the hub.

To start from scratch instead: make a new scene with a **Node3D** root, attach `res://scripts/City/CityBuilding.cs` to it, and save it in `scenes/city/buildings/`.

Every scene in that folder with the `CityBuilding` script on its root is picked up automatically when the game starts. There's no list to edit.

## How a building is placed

```
                 street
  ════════════════════════════════════   ← +Z (the front faces the street)
  ─ ─ ─ ─ ─ ─ ┐       ┌ ─ ─ ─ ─ ─ ─ ─    ← fence, with a 2.4 m gate in front of the door
               (gate)                      1.5 m front yard
  ┌──────────────┤door├──────────────┐   ← z = 0: the front face. Origin: middle of this edge, on the ground
  │                 ↑                │
  │           DoorOffset (x)         │
  │                                  │   Depth (to z = -Depth)
  │                                  │
  └──────────────────────────────────┘
  x = -Width/2                 x = +Width/2
```

In the building scene's own coordinates:
- **Origin:** the middle of the front edge, on the ground (y = 0 is the floor and the street level).
- **Front:** faces **+Z**, toward the street. The building extends back along -Z to `-Depth`.
- **Width:** spans x from `-Width/2` to `+Width/2`.
- **Up:** +Y.

The generator turns the scene to face whichever street the parcel is on, so always build it facing +Z.

It slides the building sideways so the door lines up with the gate in the fence. The building stays 1.5 m back from the street and at least 1 m from the side and back fences.

## Properties

These are set on the root node, from `CityBuilding`:

| Property | Meaning |
|---|---|
| **Type** | Who uses it:<br>• Homes: `Cottage`, `House`, `RowHouse`, `Manor`. Civilians and workers live here.<br>• Workplaces: `Shop`, `Office`, `Warehouse`. Workers work here; shops are open to the public.<br>• `Guardhouse`: guards are posted here.<br>The type also decides which parcels it can go on (see below). |
| **Width** | Meters along the street (x). |
| **Depth** | Meters back from the street (-z). |
| **Floors** | For information, and the scale lines in the editor preview: 3.2 m per floor. |
| **DoorOffset** | Where the middle of the doorway is along the front (x), in meters. 0 = centered, negative = left as seen from the street. |
| **Weight** | How often this design is picked among the designs of the same type that fit (each greybox design has 10; 0 = never). With Weight 10 and one greybox design of the type, yours is picked about half the time. Set 30 to make it about three quarters, or set the greybox one to 0 in code to replace it entirely. |

Optional **spots:** add a `Node3D` named **`Spots`** with `Marker3D` children (e.g. *Bed*, *Counter*, *Desk*). People who live or work in the building stand at these spots. Without spots, the generator picks a few points along the inside walls. Put spots on open floor, away from the doorway.

## What fits where

Parcels are made of 12 m lots in a row along the street: 1, 2 or 3 lots. Each building type only goes on certain parcel sizes:

| Parcel | Max width | Max depth | Types placed there |
|---|---|---|---|
| 1 lot (12 × 12 m) | 10 m | 9.5 m | Cottage, House, RowHouse, Shop, Guardhouse |
| 2 lots (24 × 12 m) | 22 m | 9.5 m | Cottage, House, Shop, Manor, Warehouse, Guardhouse |
| 3 lots (36 × 12 m) | 34 m | 9.5 m | Manor, Warehouse, Office |

The door also has to land in front of the parcel's gate. The gate sits at the middle of one lot, so the generator needs to slide the building until the door lines up with it, without pushing the building through a fence:
- **Up to 10 m wide:** put the door anywhere along the front, at least 1 m from the corners.
- **Wider:** keep the door near the middle. A wide building on a 2-lot parcel can't slide far.

**You don't need to work this out yourself.** When the game starts, the log reports what each design fits:

```
[city] building 'ExampleCottage': Cottage, 8 x 7 m, 1 floor(s), door at -1.5, weight 10, 3 spot(s)
[city]   fits 1-lot and 2-lot and 3-lot parcels; used on 1-lot and 2-lot (Cottage goes on 1-lot and 2-lot)
```

If a design never fits, or fits only on parcels its type isn't used on, the log shows a warning saying so.

## Requirements

- **Collision:** a `StaticBody3D` (collision layer 1, the default) with shapes for the walls, so players can't walk through them. Box shapes are best: navigation and the checks handle them well. The example puts all its shapes under one `StaticBody3D` named `Collision`.
- **A doorway at `DoorOffset`:** an opening at least 1.6 m wide and 2.4 m tall in the front wall, with nothing blocking the straight path from the street into the room.
- **Solid front wall beside the door:** at least 1.3 m on one side of the door's middle. The check tests this.
- **Stay inside the footprint.** Small things in front are fine, such as a porch, steps or a sign: there's 1.5 m before the fence. Keep everything else within the green outline, or it will poke into the neighbor's yard.
- **Ground floor at y = 0**, with an open interior. People who live or work there walk in and stand at the spots.
- **Art:** only the original dungeon art may be used (see [assets/README.md](../assets/README.md)), e.g. the brick, roof wood and hardwood textures in `assets/textures/dungeon/`. Tip: set the material's **UV1 → Triplanar** on, as the example does. Textures then tile correctly on boxes of any size without UV work.
- The generator adds a small name sign above the door at 2.75 m. Keep that spot clear if you want it readable.

## Testing a building

1. **See it everywhere it fits:** start a local session with the design favored. It then replaces the normal choice on every parcel where it fits, in all four directions:
   ```
   tools\dev-run.cmd 1 --city-favor BakerShop --city-seed 1
   ```
   Walk through the gold City Gate in the hub. A fixed `--city-seed` gives the same city each time.
2. **Automatic check:** run a scripted client with `--dev-check-city` (see the scripted-test commands in the project `CLAUDE.md`, and the `city` scenario in `tools/smoke-test.ps1`). For every parcel it checks:
   - the path through the gate and doorway is open,
   - the fence is solid,
   - the wall beside the door is solid.

   Use it together with `--city-favor`:
   ```
   [client] city check: 363/363 doors, fences and walls match the layout
   ```
   Any failure names the building and what's wrong, e.g. `Cottage 14: door path blocked at (…)`.
3. **Overview screenshot:** `--dev-overview angle` (or `top`) with `--dev-screenshot <path>`.

## Good to know

- **Server and client must have the same building scenes.** A city remembers a checksum of the designs it was made with. A client with different scenes refuses to build the city instead of building a different one. Ship building changes to both.
- **Changing a design changes which city a seed makes.** Renaming the file, or changing its size, door, weight or spots, shifts the random choices. Fine for a prototype; just don't expect old seeds to look the same.
- **Upper floors** aren't used by anyone yet (there are no stairs in the greybox ones). You can model them; people will stay on the ground floor for now.
- The greybox designs live in code: `CityLayout.Size` for their sizes, `CityBuilder` for the look. `BuildingCatalog` holds the list.
