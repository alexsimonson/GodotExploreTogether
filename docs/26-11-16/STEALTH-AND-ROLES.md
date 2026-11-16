# Stealth, roles, breaking in, grappling and lighting

What was built for [TODO.md](TODO.md) (items 0 to 4) and the follow-ups that came up along the way. Rules live in `core/` (unit-tested); the server applies them.

## Controls

| Key | What |
|---|---|
| C (hold) | Crouch: slow (2.4 m/s), quiet, harder to see |
| F | Grab the resident you're looking at; F again lets go. When a resident holds you: mash F to break free |
| Left mouse (hold, while holding someone) | Strangle them until they're subdued |
| K | Kick the door you're looking at (from outside) |
| L | Lock/unlock with a key or from inside. Without the key, with a lockpick: hold to pick (let go to stop) |
| Left click (shotgun) | A slug through a door's deadbolt from within 6 m destroys the lock |

The HUD shows how aware the city is of you (Unseen, Noticed, Suspicious, WANTED), the light where you stand (dark, dim, lit, bright), and "sneaking" while crouched.

## Roles and personality (`CityPopulation`, `NpcLoadout`)

- **Civilian**: home and errands. Minds their own home.
- **Worker**: offices and warehouses. Minds home and workplace.
- **Shopkeeper** (new): keeps a shop 7:30–18:30. Often armed (half of the armed ones keep a shotgun).
- **Police** (was Guard): two per station, day or night shift. Authority everywhere: investigate, arrest, use force on resistance. Pistol, bat or sword.
- **Security** (new): posted at a manor, office or warehouse on shift (about 60% of posts are filled). Authority over that property only. Bat, pistol or pump shotgun.

Traits, rolled per person by role: **Brave** (confronts, detains, fights), **Timid** (flees, never fights), **Nosy** (notices more, reports neighbors), **Armed** (carries something), **HeavySleeper** (only gunfire wakes them). **Grit** (0..1) is strength in a struggle.

## Seeing, hearing and suspicion (`Stealth`, `ServerGame.Stealth`)

- Five perception ticks a second. A resident sees a player if they're in the view cone (120°, 200° when alerted), within sight range, and nothing blocks the line. Sight range is 4 m + 36 m × the light where the player stands, ×0.6 crouched. Anyone within 2.2 m is always noticed. Sleepers see nothing.
- **Conduct** comes from the server's own state: trespass in a yard or inside, gun out or weapon out, crouching, running, dragging, holding someone, violence in the last 2.5 s, break-in in the last 3 s (or picking right now). Shops are public 8–18 while unlocked.
- **Suspicion** per player rises at rates set by role and authority over the place (`Stealth.Judge`), and fades when nothing's wrong. The thresholds are 25 curious, 50 suspicious, and 100 alarmed. Violence, dragging, holding someone, and (for those with authority) breaking in alarm at once.
- **Noises**: gunshot 55 m, shotgun 65, kick 28, door breaking 35, scream 22, shout 16, fight 14, running footsteps 6, steps inside a building 3.5, picking 4. Through walls they carry half as far. Kicks wake normal sleepers.
- **Bodies** seen: civilians scream and call it in, and flee if timid. The brave and nosy try to revive the unconscious; police examine the body. Anyone seen near a body becomes suspected.
- **Damaged doors** seen by owners or the nosy: "someone's broken in!", a report, then a look inside.
- **Memory**: anyone who alarmed a resident is recognized on sight from then on. A police report spreads the description to every officer and to bystanders nearby.

## Reactions (`CityNpcBrain.Reactions.cs`)

Watch → Confront (warning, about 9 s to comply) → Alarmed:

- **Police**: arrest. Run up, "Police! Stop right there!", grab, and after 5 s held you're escorted out to the hub. Breaking free is resisting, so they attack. Violence they see means immediate force.
- **Security**: like police on their post. Off it, they only react to violence.
- **Brave owners**: detain. They grab and hold you for up to 15 s while the police come. Police take over the hold, and a second officer stands by instead of grabbing.
- **Others**: flee home (or away) shouting for help, and call the police on arrival or after 8 s. Bystanders keep watching and call it in.
- **Attack**: guns from a distance (they reload from what they carry; out of ammo, they use the gun as a club), melee up close, or fists. Hit chance falls with distance, darkness, and the target running. Damage to players: pistol 18, shotgun 30–45, sword 15, axe 14, bat 12, fists 6. If they lose the target for 12 s, they search for 20 s and then go back to their day.
- **Dispatch**: each report sends the nearest free officer (two for violence). Repeats within 20 s and 20 m only update the description. The player hears "Someone called the police!"
- Police kick in locked doors they need to get through.
- **Chasing across yards**: off their routine, residents may cut straight across yards toward you, but every step is checked against the world. Walls and closed doors stop them, and they go round. They jump a fence (a 0.55 s hop, landing 1.3 m past it) when the straight line is the shorter way, unless they're timid. The timid go round through the gates.
- **Frost magic** freezes residents for 5 s, as it does dungeon enemies. A frozen resident can't move or act, lets go of anyone they hold, and can't wriggle out of a player's grip. Clients show an icy tint and "frozen". Blood magic heals the caster for each resident it hits, as with enemies.

## Grappling (`Breaching`, `ServerGame.Grapple`)

- **You grab a resident** (within 1.8 m): they're pulled along in front of you. Each second they may break free, with a chance of 5% + 30% × grit. That chance is halved if you grabbed them from behind while they were unaware, and then they can't shout either.
- **Strangling** (hold left mouse while holding someone): a meter fills over 3.5–6.5 s (by grit, 15% quicker on a surprised victim). When it's full they're subdued (unconscious, like a knockout). They can't cry out while strangled. Their escape chance starts at 140% of normal and fades to 10% as they black out. Ease off and the meter drains 15% a second. Witnesses see an attack. Victims remember you when they come round.
- **A resident grabs you**: you can move at most 0.8 m/s (enforced by the server), and you're pulled back if you get more than 1.4 m from them. Each F press is worth 1 / (6 + 9 × their grit), so 6 to 15 presses break you free.

## Breaking in (`Breaching`, `CityDoors`, `ServerGame.Grapple`)

From [DOOR-BREACHING-PLAN.md](DOOR-BREACHING-PLAN.md), with the shotgun change asked for in the TODO:

- Door state has two damage states. **Broken** (kicked in) is forced open for good and hangs at a slant. **LockBroken** (shot out) leaves the door working but unlocked forever, with a splintered hole where the deadbolt was.
- **Kick**: from outside, with a 0.8 s cooldown. A locked door takes 2 kicks (cottage) up to 6 (station); an unlocked one bursts open on the first kick. Kicks that don't break it make the panel shudder.
- **Pick**: 4–8 s by building type, 70% success. A failure breaks the pick. Moving 0.75 m, taking damage or being grabbed cancels it.
- **Shotguns**: the Double-Barrel Shotgun (1704, 2 rounds, 0.3 s between shots) and the Pump Shotgun (1705, 6 rounds, 0.9 s pump), both using 12 Gauge Slugs (1706). Range 40 m. The deadbolt has its own small hitbox above the handle, and a slug through it from within 6 m destroys the lock. Other guns do nothing to it.
- **Lockpick**: item 1703, stackable. Police sometimes carry them.

## Lighting and alleys (`CityLighting`, `CityLayout`)

- Daylight curve shared by client and server, moonlight 0.06 at night. Ambient at night is much darker than before.
- Street lamps stand on about 55% of street cells at a sidewalk corner, with an 11 m radius, on from dusk.
- House lights: a building is lit after dark while someone inside is awake. This is sent with the door flags, and the client turns on a warm interior light and glowing windows.
- **Alleys**: 2.6 m passages between neighboring single-lot parcels in the commercial zone (about half the candidate edges), when at least one end opens onto a street. Each lot gives up 1.3 m. They're public ground, unlit, and shaded even by day. Residents never walk them. Layout format version 3.

## Movement validation (security review, findings 4 and 5)

`MoveGuard` keeps a distance budget at the player's current top speed: sprint 8, crouch 2.4, dragging or holding someone 2.2, held 0.8. Up to 1.5 s of unused budget can build up, plus 0.75 m slack, and climbing is limited separately. The server also refuses any move whose straight line crosses a wall, fence or closed door at waist height. A rejected move sends the client back to the last accepted position. Reach checks, "from inside" locking and dragging now only ever see accepted positions.

## Dev and tests

- Server `--dev-oblivious`: residents ignore players (used by older scenarios that test something else).
- Clients: `--dev-breach`, `--dev-trespass`, `--dev-grab` (`--dev-strangle` to strangle once held), `--dev-struggle`, `--dev-shoot-air` (with `--dev-shoot-delay S`).
- Smoke scenarios `breach` and `stealth`. Unit tests in `tests/.../StealthTests.cs`.

## Known limits and next ideas

- Residents move on the ground plane. Upper floors and rooms are the next step.
- Nobody repairs broken doors or locks yet (the locksmith could, and owners could notice the next morning).
- Being arrested costs nothing beyond being taken out of the city. Confiscating the weapon is an option.
- Hearing doesn't identify the shooter. Only being seen does.
