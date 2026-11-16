# Plan: lockpicking, kicking doors, shotgun breaching

Three ways past a locked city door without its key. Each one builds on `CityDoors`, which owns door state on the server, and on `CityDoor`, the hinged door node. The server decides every outcome, as with everything else.

## Shared groundwork (do first)

- **A third door state: `Broken`.** The door is forced open, can't be closed or locked, and the panel hangs at a slant or is gone.
  - Add it to `CityDoors.DoorState` and to the `DoorStates` triples, which become quads.
  - Residents treat a broken door as open. When they find one, it's a good hook for the stealth phase: "noticed a broken door".
- **Noise events** (needed for stealth later): `CityNoise.Emit(position, loudness, cause)` on the server. For now it only logs. Later, residents within range react.
- **New door actions:** a `DoorAction(interactionId, action)` request (pick, kick), with a rate limit. Breaching goes through the existing attack path.
- **Smoke scenario `breach`:** pick, kick and shoot three locked doors. Check each door's new state and that the server logs the noise.

## 1. Lockpicking (quiet, slow, can fail)

- **Item:** Lockpick (id 1703, stackable). It goes in the debug supply; guards and some workers may carry them.
- **Input:** hold **L** on a locked door when you have no key but do have a lockpick. A progress bar runs for 4–8 s, longer for manors, offices and guardhouses.
  - Moving away, or taking damage, cancels the attempt.
  - On the server it's a timed action, so the client can't skip ahead: a start request, then the server checks the player is still near the door when the time is up.
- **Outcome:**
  - **Success** (70% base): the door is unlocked, not broken, so nobody can tell. It makes a small noise.
  - **Failure:** the pick breaks (one fewer in the stack).
  - Later, a "Lockpicking" skill can shorten the time and raise the chance (the original `SkillSystem`).
- **Code:**
  - The rules go in `core/City/Breaching.cs`: time per building type, success roll from a seeded `Random`. These get unit tests.
  - The server gets a `ServerGame.Breaching.cs` partial.
  - The client gets a HUD progress bar, reusing the notice label style.

## 2. Kicking doors (fast, loud, may take several tries)

- **Input:** **K**, or a melee attack with nothing equipped, on a closed door from outside.
- **Each kick:**
  - The door has hidden "strength" by building type: cottage 2 kicks, house 3, manor/office 4, guardhouse 6.
  - Every kick costs one point. At zero the door is `Broken` and swings open.
  - Each kick is a loud noise. On the client, the panel shudders: a short rotation pulse.
- **Locked or not:** kicking works on any closed door. An unlocked one simply bursts open on the first kick (`Broken`).
- **Code:** the server keeps a per-door `KickDamage` in `CityDoors`; the strengths go in `core/City/Breaching.cs`. A 0.8 s cooldown between kicks.

## 3. Shotgun breaching (instant, very loud)

- **Item:** Shotgun (id 1704), class `Gun`, with a `Breaches = true` flag on `ItemDef` (like `KnocksOut`).
  - Magazine 2, ammo Shotgun Shells (id 1705, stackable).
  - Short range (15 m), heavy damage to people.
- **Breaching:** a hit-scan that strikes a closed door at close range (3 m or less) breaks it at once: `Broken`, panel blown open.
  - Any other gun just leaves the door as it is: the shot is stopped by the closed door, as now.
- **Code:**
  - In `ServerGame.Combat.HitScan`, if the collider is a `CityDoor` and the weapon `Breaches`, call `BreachDoor(...)`.
  - Very loud noise.
  - The client shows a muzzle flash (later) and the door swings hard.
- **Art:** none exists, so the shotgun gets no icon, like the bat.

## Order and size

1. Groundwork: the `Broken` state, the quads, a noise stub, the request and the smoke scaffold. Small.
2. Kicking: the simplest end-to-end path, with no item. Small.
3. Shotgun breaching: reuses the gun code. Small to medium.
4. Lockpicking: a timed action with progress UI. Medium.

Each step ends with unit tests for the rules in `core/` and a smoke check.

## Open questions

- Can a broken door be repaired, by residents the next day or by the locksmith?
- Do residents call guards when they hear a kick or a shot? (Stealth phase.)
- Should a lockpick also work on a body? No, keys already cover that. Leave it out.
