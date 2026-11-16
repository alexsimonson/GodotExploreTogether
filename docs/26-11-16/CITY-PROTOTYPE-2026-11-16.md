# City prototype: plan (2026-11-16)

**Decisions (2026-11-16):**
- The city does **not** replace the hub. It's a separate instance you travel to.
- A **new city each visit**, recreatable from its seed (like dungeons).
- The test city is **5×5 tiles** (180 m).
- **Trespass detection is the MVP.** What happens after detection comes later.
- Cities are fairly open. Fences mark property and are the natural barriers.

A rapid prototype on top of the current game: a generated city made from the existing recursive backtracker, with greybox buildings and NPCs who live and work in them. It is built as a new instance kind (`city`) next to the hub and dungeons, so networking, players, navigation and the dev/test tooling all carry over.

Work on a git branch (`proto/city`) so the main line stays clean.

## 1. Streets from the maze

`Maze.cs` stays untouched. A new `core/City/CityLayout` runs it and reads the result differently:

- **Each maze node is a city tile** (about 36 m instead of 5 m). The street layout is exactly the maze: where the dungeon had a corridor, the city has a street.
- **A tile is a 3×3 grid of 12 m subcells:**
  - The center is always street.
  - Each side subcell (N/S/E/W) is street if the maze connects that way. If not, it becomes a **lot** facing the center street. A dead end, for example, gets three lots around a little square.
  - The 4 corner subcells are always lots. Each faces whichever neighboring street arm exists, or becomes a backyard or garden if none does.
- A 12 m street holds an 8 m road with 2 m sidewalks.
- Buildings meet back to back across tile edges, so the city reads as dense blocks with winding streets.
- **Sizes:** 5×5 tiles (180 m) is the test city, 10×10 (360 m, 100 tiles) a full one. The maze recursion is one stack frame per node, so it's fine at these sizes.
- **Seeded, like dungeons:** the server sends (seed, width, height, length) and each client rebuilds the same city. Lot and building choices use the same seed, so only the parameters cross the network.

**Openness:** a perfect maze has no loops. `CityLayout` therefore opens extra street connections between neighboring tiles: `loopPercent`, 15% by default, 0 gives the pure maze. Where the maze leaves no street, the barrier is a **property fence**, not a wall. A dead end is a street that ends at people's yards.

**Lots and parcels:** each lot cell joins a parcel, a fenced property with one building whose door faces the street:
- 1 cell (12×12 m).
- Side cell + corner (12×24 m): a corner with no street beside it becomes that house's back garden.
- The full closed side of a tile (36×12 m), for offices, warehouses and manors.

Every parcel touches a street (tested).

The whole city is visited (length = width × height), so there are no gaps. Parks can come later.

`CityMap.Render` prints a text map for review.

## 2. Buildings

**Zoning:** a tile's maze depth (its distance from node 0 along the street tree) or its distance to the city center picks the zone. Lots in the core are commercial and civic, lots near the edges residential.

**Building types** (greybox: boxes with a door, a roof color and a size per type):

| Type | Footprint | Who belongs there |
|---|---|---|
| Cottage | 1 lot, 1 floor | 1–2 civilians (owners) |
| House | 1 lot, 2 floors | 2–4 civilians |
| Row house | 1 lot, narrow, tall | 1–3 civilians |
| Manor | 2–3 merged lots | 2–4 civilians + a guard |
| Shop | 1 lot, ground floor | Shopkeeper (worker), lives elsewhere |
| Office | 3 merged lots (a closed side of a tile) | 3–6 workers + 1 guard |
| Warehouse | 3 merged lots | 2–4 workers |
| Guardhouse | 1–2 lots, one per district | Guards (their post and base) |

- **Merged lots:** a closed tile side has corner + side + corner in a row, a 36×12 m strip, which fits the large types.
- **Every building is enterable:** hollow walls, a door gap facing its street and a simple interior floor, so NPCs (and players) can go inside and the navmesh reaches in.
- **Placeholder look:** procedural boxes with per-type colors and slight size variations.
  - The art rule allows only the original dungeon art, so no new models.
  - The dungeon wall and floor textures could go on the walls for a stone-town feel.

## 3. NPCs

**Population** (`core/City/CityPopulation`, seeded, unit-tested). Each NPC has an id, a name, a role, a home and, where relevant, a workplace.

- **Civilian:** owns or lives in a home. Day: walks around, visits shops, goes home. Night: at home.
- **Worker:** lives in a home and works at a shop, office or warehouse. Commutes on the game clock.
- **Guard:**
  - Assigned to a guardhouse, office or manor.
  - Patrols the streets near the post, or stands at its door.
  - Responds to trespass.

**Authorization:** each building keeps an access list:
- The residents (owners), its workers, and the guards posted there.
- Guardhouses: all guards.
- Shops: anyone, but only while open.

The same rule (`CityAccess.IsAllowed(who, building, time)`) answers this for NPCs and players.

**Brain:** `core/City/CityNpcBrain` is a state machine in the style of `EnemyBrain`, plain C# and unit-tested. Its states:
- At home
- Commuting
- At work
- Wandering
- Patrolling
- Investigating
- Escorting out / chasing (guards)

It runs on the server (`CityNpc`, using `NavigationAgent3D`) and is replicated per instance like enemies.

**Game clock:** one per city instance. A fast day for testing (e.g. 10 real minutes) drives the schedules, and is replicated so clients can tint the light.

**Trespass (MVP: detection only).** A parcel is the whole property: building, yard and garden. Being inside one without access is trespassing. Detection has two levels, both decided on the server:
- **Trespassing:** the player is on a parcel they have no access to. This is a simple lookup (`CityLayout.ParcelAt`).
- **Seen trespassing:** an NPC with access (an occupant, or a guard) has the player in view, using `EnemyBrain`'s vision rule.

For now, both show as a HUD indicator and a server log line, e.g. "Kermit is trespassing on 'House #14' (seen by Guard Bo)". The consequences (warnings, guards, combat, a wanted state) come later. Later still, players could own or rent a house and appear on its access list.

## 4. Milestones

Each one ends with something runnable and a screenshot. Trespass detection is pulled forward: the parcel check doesn't need NPCs.

1. ✅ **Layout (core only):** `core/City/CityLayout`, which turns maze tiles into streets, lots, parcels, buildings, doors, zones and guardhouses, plus `CityMap` (a text map).
   - 11 tests:
     - Same seed gives the same city.
     - Every cell is street or in exactly one parcel.
     - All streets are connected.
     - Every parcel fronts a street.
     - Buildings stay inside their fences.
     - `ParcelAt` covers the whole parcel.
     - Guardhouses are spread out.
   - A 5×5 city has about 120 parcels and 3 guardhouses.
2. ✅ **Greybox city instance:**
   - `scripts/City/CityBuilder`: ground, roads, sidewalks, 0.9 m fences with a gate gap in front of each door, and hollow buildings.
     - Buildings have a doorway, a ceiling over the ground floor, a roof, window panes, per-type colors, battlements on guardhouses, and a sign over the door ("House 14").
     - A 4 m wall surrounds the city.
     - All boxes go into one vertex-colored mesh (one draw call), plus one static body of box colliders.
   - Instance kind `city`:
     - The hub's **City Gate** (gold, beside the Dungeon Entrance) joins the city that has players in it or arriving, otherwise it generates a new one.
     - A gate at the start junction leads back.
     - Empty cities close after the dungeon timeout.
   - Server args: `--city-size N` (default 5), `--city-seed S`, `--city-loops P`.
   - Client dev flags:
     - `--dev-city`: go through the gate.
     - `--dev-overview angle|top`: overview camera for screenshots.
     - `--dev-check-city`: raycasts every doorway (open), fence beside the gate (solid) and front wall (solid).
     - `--dev-return-after S`: now also works in the city.
   - Measured: 5×5 builds in about 50–80 ms (about 120 parcels), 10×10 in about 150–200 ms (about 490 parcels).
     - City check: 363/363 (5×5, seed 1), 354/354 (seed 7), 1464/1464 (10×10, seed 3).
   - Smoke scenario `city`: two players end up in the same city, its geometry passes the check, both return, and the city closes.
3. ✅ **Custom buildings** (added on request): building scenes in `scenes/city/buildings/` join the randomization.
   - `CityBuilding` is the root script, with an editor preview of the footprint. `CityCatalog` loads the scenes.
   - `BuildingCatalog` (core) holds the designs. A checksum travels with the city, so server and client must match.
   - A fit report runs at startup. `--city-favor NAME` places a design wherever it fits.
   - Example: `ExampleCottage.tscn`. Guide: [docs/CITY-BUILDINGS.md](../CITY-BUILDINGS.md).
4. ✅ **Residents and schedules** (moved ahead of trespass on request):
   - `core/City/CityPopulation`: about 1.6 people per tile (40 for 5×5, 160 for 10×10).
     - 2 guards per guardhouse, who live there.
     - Workers: about 40% of the rest, filling shop (1), office (4) and warehouse (3) jobs.
     - Civilians: everyone else.
     - Households of 1 up to the home's capacity, sharing a family name.
   - Access rule: home, workplace, and public parcels (shops). This is the basis for trespass.
   - `core/City/CityPaths`: no navmesh needed (the old milestone "Navigation" is dropped for NPCs).
     - Routes use only street cells (with a personal lane offset), plus the straight way through the person's own gate and door to a spot inside.
     - So residents never cross other people's property.
   - `core/City/CityNpcBrain` with `CityClock`:
     - Civilians: home until 8, errands (nearby shops, walks) until 12, lunch at home, errands 13 to 18, home.
     - Workers: at work 7 to 17, errands until 18:30, then home.
     - Guards: day or night shift patrolling streets within about 100 m of their post, resting in the guardhouse off shift.
     - Everyone shifts their times by up to ±45 minutes.
   - Server: `CitySimulation` (one per city, brains at 20 Hz) drives the replicated `CityNpc` nodes. They are placeholder bodies colored by role: guards in blue with helmets, workers in browns with caps, civilians in varied colors. Each has a name and activity tag.
   - The clock is sent to clients on arrival and every 5 s. The HUD shows "City time 09:42". Sun, sky and ambient light follow the clock, with dark nights.
   - Server args: `--city-people N`, `--city-start-hour H` (default 0, midnight), `--city-day-minutes M` (default 48, so one game hour is 2 real minutes), `--dev-log-npcs`.
   - Client: `--dev-overview near`. `--dev-check-city` also reports the residents in view.
   - Tests:
     - Over a simulated day, nobody is ever on a parcel they may not be on.
     - At least 90% of workers are at work at 11:30. Everyone is home at 03:00, except night guards on patrol.
     - People visit shops.
     - A deliberately broken router is caught.
   - The smoke scenario checks that 40 residents and the clock reach the client.
   - **Collision** (`core/City/CityCrowd`, residents 0.35 m radius, players 0.4 m):
     - Players can't walk into residents. Each client blocks its own player, like players and enemies.
     - Residents don't walk into players or each other.
     - On streets, a blocked resident sidesteps (right first, so head-on pairs pass), onto open street only.
     - In yards, gateways and doorways they wait. After 2.5 s blocked by another resident they squeeze past along their planned line. They never pass a player: a player in a doorway makes them wait.
     - Two people arriving at one spot stop side by side and edge apart.
     - Tests:
       - Overlaps in a busy 4-hour morning: 6–7 with collision, 30–52 without; the limit is 15.
       - Residents never come within 0.75 m of a player, even one blocking their doorway.
       - Residents walk around a player standing in their way.
       - The no-trespass rule still holds with sidestepping.
     - Smoke scenario `crowd` (`--dev-chase-npc`): a player running at residents is stopped at 0.73–0.74 m.
5. ✅ **Doors, keys, resident inventories and loot** (added on request):
   - **Doors** (`core/City/CityDoors`, `scripts/City/CityDoor`): one per building, in the doorway. Open or closed, locked or unlocked.
     - They swing one way only: inward, hinged on the left as seen from the street.
     - E opens or closes. L locks or unlocks: from outside only with the key, from inside freely (a thumb-turn, so nobody gets locked in). Open doors can't be locked.
     - The server owns the state and sends every door on arrival, then each change. Collision follows the panel: a closed door blocks players and bullets.
   - **Keys** (item 1700): the item's data is the lock code. Each resident carries keys to their home and workplace; guards to their guardhouse.
   - **Residents use doors:** they open their way in and out (unlocking with their own key), close behind them, and lock up:
     - when they're the last one out who belongs there,
     - at night when they come home,
     - at bedtime.
     - Someone who unlocked a door from inside to get out locks it again behind them.
     - Locked out without a key, they turn back. They avoid locked shops.
     - Doors start locked where nobody is inside. Tests: at 03:00 every door is shut and every occupied home locked; empty shops are locked at 21:00; a household without keys stays out.
   - **Resident inventories** (`core/City/NpcLoadout`), using the player's `Inventory` and `Gear`. The Unity NPCs only had a weapon slot; this uses the player's system as asked.
     - Keys, a shirt and pants, some coins, sometimes a health kit; warehouse workers carry an axe.
     - Guards wear a helmet and carry a sword, or about 30% of the time a pistol with spare rounds.
   - **Residents can be hurt and killed:** a hitbox on the enemies' physics layer, so melee, guns and magic hit them, with 100 health. A body lies where it fell, and E searches it.
   - **Loot panel:** next to the inventory, titled "Body of …", with "Take all".
     - Take-only (`SlotContainerLogic.IsAllowedForLoot`): into empty slots or onto the same stack, never putting anything into a body.
     - Taken items are registered like dungeon pickups: a serial for non-stackables, a `picked_up` event "from the body of …", saved at once.
   - Dev:
     - Server: `--dev-kill-npcs N` (bodies to search).
     - Client: `--dev-doors` (search, take all, lock, unlock, open and close with the key, try a door without its key), `--dev-fight` also works in the city (shoots visible residents), `--dev-open-nearest-door`.
   - Smoke scenario `doors` covers the whole flow, plus the city check that every closed door blocks its doorway.
   - Deferred to the NPC depth phase: residents reacting to violence, guards responding, and any law or ownership consequences.
6. ✅ **Dragging bodies and knocking people out** (added on request, groundwork for stealth):
   - **Dragging:** G on someone who's down (dead or knocked out) takes hold of them; G again lets go.
     - The body trails the player on a 2.1 m "rope", head first, swinging round corners (`core/City/BodyDrag`, tested).
     - The server moves it each physics frame from the player's position, with a ray so it never passes through walls, fences or closed doors.
     - While dragging, the player walks at 2.2 m/s, can't sprint, and can't jump, so the only way past a fence is through a gate.
     - Only one player can drag a body at a time. Leaving the city or disconnecting lets go.
     - The brain keeps the body's position, so residents can discover bodies once stealth arrives.
   - **Knocking out:** new blunt weapons, the Bat (1701) and the Concrete Block (1702), with `ItemDef.KnocksOut`. A hit knocks a standing resident out without hurting them.
     - They drop and can be searched and dragged like a body. A lethal weapon kills them.
     - They come round after `--city-wake-seconds` (90 by default). Whatever wasn't taken goes back on (`NpcLoadout.FromLoot`).
     - Then they find their way back to the street (`CityPaths.WayToStreet`): out of a building through its door (unlocking from inside, relocking the latch), round the house if it's in the way, out through the gate, never through walls or fences. Then they carry on with their day.
     - Tests: from behind a house and from inside a locked house (a mutation check confirms the detour is needed), and carrying on to work afterwards.
     - Guards sometimes carry a bat instead of a sword; warehouse workers often carry a concrete block. The dev starter kit includes a bat.
   - Dev:
     - Server: `--dev-knockout-npcs N`, `--city-wake-seconds S`.
     - Client: `--dev-drag` (grab the nearest body, walk 4 s, let go). `--dev-fight` with `--dev-weapon Bat` and `--dev-chase-npc` knocks residents out.
   - Smoke scenario `drag`.
   - Later (stealth phase): who notices a body, or someone being dragged or knocked out; hiding bodies; noise.
7. **Trespass detection (players):**
   - The server tracks which parcel each player is on and flags trespass, using the access rule above. Players have no home yet, so only shops are allowed.
   - HUD indicator and log line.
   - Smoke scenario: a headless client walks into a yard and is flagged, then walks back to the street and the flag clears.
8. **Seen trespassing:**
   - The people who belong on a parcel (`CityPopulation.PeopleOf`) and guards check vision (`EnemyBrain`'s rule) and report who saw the player.
   - Smoke scenario: a guard sees a player in a yard.

**Performance watch:** 160 residents in a 10×10 city run fine. With many cities at once, update residents far from any player less often.

## 5. Open questions

- **Loops:** is 15% extra streets the right openness? Judge it from the greybox (milestone 2).
- **Fences:** they're 0.9 m, so players can hop them (the jump reaches about 1 m). Raise them to make trespassing harder.
- **Shops:** public all the time for now. The clock exists now, so opening hours are easy to add.
- **Residents:** a player standing in a doorway blocks it indefinitely. Should residents give up and go elsewhere after a while? Should upper floors be used?
