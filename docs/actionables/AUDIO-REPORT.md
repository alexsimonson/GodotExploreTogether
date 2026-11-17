# Audio: what exists and what to source

## How the game plays sound now

- **Server-sent sounds.** When something audible happens (a shot, a hit, a door, a scream), the server sends a `Sound(id, position)` message to the players within that sound's range. The IDs are listed in `core/Audio/Sounds.cs` (`SoundId`). Server hooks are in `scripts/Server/ServerGame.Audio.cs` and at each event.
- **Client-only sounds.** Footsteps for every character (players, residents, monsters) within 25 m of the camera, ambience for each place (hub, dungeon, city by day and by night, crossfaded by the city clock), music from each tavern while it's open (16:00 to 3:00), torch crackle in dungeons, alert stingers, and the inventory open/close sounds. All of this is in `scripts/Audio/GameAudio.cs`.
- **Buses:** SFX, Footsteps, Ambience, Music and UI all feed into Master. The client args `--mute` and `--volume DB` control Master.
- **F1: sound monitor.** In game, F1 shows every sound playing right now, grouped by name: what it's meant to imitate, whether it's a real file, a stand-in or a placeholder, how many are playing, how far away the nearest is, and whether it's faded out (`silent`). Short sounds stay listed for 1.5 s after they end. Opening it also prints the list to the log. `--dev-sound-monitor` opens it from the start. The descriptions live in `scripts/Audio/SoundMonitor.cs` (`Meant`): add one there for each new sound.

### Adding or replacing a sound

Sounds are looked up **by name**:
1. `assets/audio/NAME.wav` (or `.ogg` / `.mp3`). Variants named `NAME_1`, `NAME_2` (up to 8) are picked at random.
2. If no real file exists, the generated placeholder `assets/audio/placeholder/NAME.wav` is used.

To replace a placeholder, put a real file in `assets/audio/` with the same name, then reimport (`--import`). You don't need to change any code. Loops (ambience, music, crackle) are set to loop in code, so any clip will work, but it should loop seamlessly.

The placeholders come from `tools/generate_placeholder_sounds.py` (rerunnable, standard library only). They're synthesized blips, thuds and noise, crude on purpose, so you can hear every hook working before the real sounds exist.

## Existing sounds (from the Unity project)

Only three sound files exist, all in `Assets/Resources/Audio`. Each is 24-bit, 44.1 kHz stereo.

| Unity file | Length | Unity use | Now used as |
|---|---|---|---|
| `gunshot.wav` | 0.89 s | `Gun.attackSound` (the pistol) | `gunshot`: pistol shots, players' and residents' |
| `rifle shot.wav` | 0.51 s | Not loaded by any script | `rifle_shot`: **stand-in for shotgun shots** until a shotgun blast is sourced |
| `reload.wav` | 1.50 s | `Gun.reloadSound` | `reload`: pistol reloads, and a **stand-in for shotgun reloads** |

All three were copied into `assets/audio/`. The Unity project had no other audio: no music, ambience, footsteps or UI sounds. Melee and tool attack sounds were commented out in `Weapon.cs`/`Tool.cs`.

## Sounds to source

Priority: **A** = heard constantly or tied to core actions, **B** = common events, **C** = polish.

### Weapons and combat
| Name | Pri | When | Notes |
|---|---|---|---|
| `shotgun_shot` | A | Double-barrel and pump shotgun shots | Uses `rifle_shot` until sourced. Heavier and boomier than the pistol. |
| `shotgun_reload` | B | Shotgun reloads | Uses `reload` until sourced. Shells sliding in, or a pump rack. |
| `dry_fire` | B | Trigger with an empty gun | A click. |
| `swing` | A | Every melee/tool swing (sword, bat, axe, concrete block) | 3+ variants (`swing_1..3`). Could split into blade and blunt later. |
| `hit_flesh` | A | Weapon hits a resident | 3+ variants. |
| `hit_blunt` | A | Knockout blow (bat, block); city fistfights | 3+ variants. |
| `chop` | B | Axe on a tree (gathering) | Wood chop. |
| `magic_ice` | B | Ice Wand cast | Crystalline whoosh. |
| `magic_blood` | B | Blood Wand cast | Wet, low and ominous. |
| `furniture_break` | A | A chair or stool smashed over someone (it breaks on the first hit) | Splintering wood, louder and drier than `door_break`. |

### Creatures and people
| Name | Pri | When | Notes |
|---|---|---|---|
| `enemy_attack` | A | A dungeon enemy hits a player | Growl or snarl. |
| `enemy_hurt` | A | A dungeon enemy is hit | Variants. |
| `enemy_death` | B | A dungeon enemy dies | |
| `player_hurt` | A | A player takes damage | Grunts, 3+ variants. |
| `player_death` | B | A player dies | |
| `npc_hurt` / `npc_death` | A | City residents hurt or killed | Male and female variants would help (`npc_hurt_1..4`). |
| `scream` | A | A resident screams (alarmed, fleeing, attacked) | Several variants; these carry far (50 m). |
| `grab` / `break_free` | B | Grappling | Cloth rustle and struggle grunts. |

### Doors and locks (city)
| Name | Pri | When | Notes |
|---|---|---|---|
| `door_open` / `door_close` | A | Any door opens or closes (players and residents) | Heard constantly in the city. Wooden door and latch. |
| `door_lock` | B | Locked or unlocked | A key turning or a deadbolt. |
| `door_kick` | A | A kick that doesn't break the door | Heavy impact on wood. |
| `door_break` | A | A door kicked in, or its lock shot out | Splintering wood. |
| `lockpick` | B | While picking (the lockpicking noise) | Quiet metal scratching. |
| `lockpick_break` | C | A lockpick snaps | |

### Items and world
| Name | Pri | When | Notes |
|---|---|---|---|
| `item_pickup` / `item_drop` | B | Ground items picked up or dropped | |
| `furniture_grab` | C | Picking up a chair or stool in a city building | A scrape of wood on the floor. |
| `flashlight_click` | B | Flashlight switched on or off (T) | Small plastic click. |
| `exit_open` | B | The dungeon exit unlocks | Stone grinding. Heard across the dungeon (80 m). |
| `travel` | C | Arriving anywhere (gate or entrance) | Whoosh or footsteps through a gate. |

### Power and lights (city)
| Name | Pri | When | Notes |
|---|---|---|---|
| `glass_break` | A | A street lamp, porch light or ceiling lamp is shot out or hit | Bulb pop plus glass tinkle. 3 variants would help. |
| `light_switch` | B | A building's light switch flipped (players, and residents switching it back on) | Wall switch click. Quieter and deeper than `flashlight_click`. |
| `fusebox_bang` | B | A fuse box hit or shot until its lock gives | Metal door clang. |
| `breaker` | B | A fuse box's breaker flipped (a building's power cut or restored) | Heavy switch clack. |
| `sparks` | B | A district substation cut or restored | Electrical crackle and fizzle, then the hum dies. Carries 40 m. |
| `main_breaker` | B | The power station's main breaker (the whole city) | Big lever clunk, then a generator winding down. Carries 50 m. |

### Footsteps (client-side, played every 0.75 m of movement)
| Name | Pri | Where |
|---|---|---|
| `step_stone_1..4` | A | City streets and yards |
| `step_wood_1..4` | A | Inside city buildings, and dungeon floors (hardwood) |
| `step_dirt_1..4` | A | The hub |

Four or more variants each. These play constantly, so they must be short (under 0.2 s) and quiet.

### Ambience and music (loops)
| Name | Pri | Where | Notes |
|---|---|---|---|
| `amb_city_day` | A | City by day (fades with daylight) | Distant town murmur, birds, carts. 30 s or more, seamless. |
| `amb_city_night` | A | City at night | Crickets, wind, a distant dog. |
| `amb_dungeon` | A | Dungeons | Low drone, drips. |
| `amb_hub` | B | The hub | Outdoor wind, birds. |
| `tavern_music` | B | Each tavern's door, 16:00 to 3:00 (heard within ~35 m) | Lively folk loop. The placeholder is a synthesized tune. |
| `torch_crackle` | C | Every dungeon torch (heard within 7 m) | Soft fire crackle. |

### UI and stealth cues
| Name | Pri | When |
|---|---|---|
| `alert_suspicious` | B | Your city alert rises to suspicious |
| `alert_alarmed` | B | Your city alert rises to alarmed (someone's calling the police) |
| `ui_open` / `ui_close` | C | Inventory open and close |

## Not hooked up yet (later candidates)

- Residents' speech: shouts like "Police! Open up!" are shown as text only. Barks or "voice blips" would help.
- Power hum: a quiet electrical hum near substations and the power station, which stops when they're cut, would make an outage noticeable by ear.
- City-wide blackout cue: a distant "power down" (generators winding down, a crowd murmur) when the main breaker is pulled, heard everywhere in the city.
- Bullet impacts (ricochets off stone and wood) at the tracer's end point.
- Different footsteps for crouching, which are quieter: the walker currently plays one volume for every gait.
- Settings: there's no volume menu yet. The buses exist, so sliders are a small addition.

## Changes since the first version of this report

- Furniture, the flashlight, and the power grid got their own sounds: `furniture_break`, `furniture_grab`, `flashlight_click`, `glass_break`, `light_switch`, `fusebox_bang`, `breaker`, `sparks`, `main_breaker`. Each has a generated placeholder, listed in the tables above.
- Fixed: the item pickup sound was read from an item that had already been removed (an engine error on every pickup).
- F1 sound monitor (see above).
- Placeholder count: 59 files in `assets/audio/placeholder/` (rerun `python -I tools/generate_placeholder_sounds.py` after adding new ones, then `--import`).

## Where to get sounds

CC0 sounds (no attribution needed) are the simplest to use: Kenney's audio packs (impact, RPG and UI sounds), freesound.org filtered to CC0, and the yearly Sonniss GDC bundle (royalty-free, commercial use allowed). For CC-BY sounds, keep a credits list in `assets/audio/CREDITS.md`.
