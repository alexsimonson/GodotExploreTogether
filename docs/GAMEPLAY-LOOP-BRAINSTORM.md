# Gameplay loop brainstorm: the city sandbox

Status: **brainstorm, nothing decided.** Edit freely; strike things out, add `[YES]` / `[NO]` / `[MAYBE]` tags. Written 2026-11-16.

## 1. The problem

The city already has a lot of *verbs* (sneak, trespass, pick/kick/shoot locks, grab, strangle, drag bodies, loot, cut power, break lights, pick up chairs) and a living *world* (residents with homes, jobs, sleep, roles, traits, suspicion, witnesses and gossip). What it lacks is **a reason to do anything**. Players have no stakes, no direction, no payoff.

## 2. Design stance: sandbox, not quest list

Principles to test every idea against:

1. **Systems generate stories; we don't script them.** Prefer a rule that makes many situations over a mission that makes one.
2. **Give players goals they can pick, not goals we assign.** Offer a menu of *hooks*, never a main path.
3. **Every action should have a visible consequence in the world** (a resident remembers, a door stays broken, the police change posture). Consequences are the real content.
4. **Co-op is the point.** Ideas that need 2+ people with different jobs beat ideas where co-op is just "more guns".
5. **Failure is fun, not a reset.** Getting caught should create a new situation (escape, bail, a chase), not a loading screen.
6. **Cheap to build on what exists.** Lean on suspicion, witnesses, loot, keys, power and the day cycle already in the game.

## 3. What we already have (the toolbox)

| System | Gameplay hook it offers |
|---|---|
| Day/night clock, sleepers, heavy sleepers | Time as a resource: *when* you act matters |
| Suspicion tiers (Unseen, Noticed, Suspicious, WANTED) | A built-in risk meter |
| Light level + crouch + noise radii | Stealth skill expression |
| Witnesses, descriptions, outfit colour, telephone-game gossip | Disguise, alibis, "who saw me?" |
| Locks, keys, lockpicks, kicks, shotgun breach | Many ways in, each with a different noise/risk |
| Bodies, grab, strangle, drag, revive | Moral and logistical complications |
| Power grid (fuse boxes, substations, main breaker) | Environmental control, area denial |
| Roles: civilian, worker, shopkeeper, police, security | Different "locks" on different places |
| Item serials and history (`--item-history`) | Provenance: *this* item has a story |
| Hub, dungeon, storage, gathering, hardcore | A home base and a second activity to bring back loot from |

## 4. The core loop (candidate)

A loop that works for a sandbox is small and repeatable. Candidate:

> **Prepare (hub) -> Go to the city -> Do something risky -> Get out with something -> Convert it into capability -> Prepare differently.**

The open questions are *what the something is*, *what it converts into*, and *what keeps the risk real*. The sections below are menus for each.

## 5. What is the "something"? (objectives players can choose)

### 5.1 Heists and theft
- **Loot score**: every building has a stash with a value shaped by who lives there (a manor beats a cottage, a shop till beats a bedroom). Plain, robust, and the baseline.
- **Targeted heist**: the game posts a "known valuable" (a safe in manor X, a ledger in office Y). Players case it, plan it, do it.
- **Shopkeeper takings**: shops accumulate cash over the day; closing time is the vulnerable moment. Rob it (gun, counter), or sneak in after hours.
- **Pickpocketing**: a lower-stakes, always-available verb for solo play.
- **Warehouse jobs**: a crate that must be carried (two-hand rule!) from a warehouse to a drop point without being seen. Heavy items slow you and block weapons.

### 5.2 Mischief and chaos (the "make your own fun" bucket)
- **Pranks**: rearrange furniture, swap lights off, move a resident's chairs. Score the *reaction* (a Nosy neighbour investigating).
- **Blackout**: take down a district's power at dusk and watch the city react. Great co-op set piece.
- **Riot / diversion**: one player makes noise on one side of town so another can work on the other. Rewards coordination.
- **Vigilante**: the opposite fantasy. Catch *NPC* burglars (see 7.2), or stop a crime in progress.
- **Hide and seek / cops and robbers**: a player-versus-player or player-versus-police mode on the same systems. The "WANTED" tier already makes a chase.

### 5.3 Stealth and infiltration as a pure skill game
- **Ghost run**: finish a building without ever leaving Unseen. Leaderboard-free pride.
- **Speedrun the block**: reach point B through N yards undetected.
- **Photograph / document**: a camera item; capture evidence of resident routines (feeds 5.4).

### 5.4 Information as treasure
- **Casing**: residents have routines. A notebook logs who sleeps where, when guards rotate, who's a heavy sleeper. Information is the real progression, and it's per city seed.
- **Rumour economy**: witnesses spread descriptions; players can *plant* a false description (swap shirt colour, then be seen elsewhere). Alibi puzzles for free.
- **Overheard tips**: eavesdrop on a conversation to learn that "Mr. Ames keeps cash under the floorboards".

### 5.5 Social and role-play
- **Be a resident**: a no-crime option. Rent a house, keep it, host friends, work at a shop for pocket money. The city should be pleasant to *live* in, or crime has no contrast.
- **Be the law**: sign on as police/security for a shift; patrol, respond to NPC incidents. A natural role for one friend while others rob.
- **Drive the story as a group**: shared goals like "own the manor on Elm St", "get the whole crew rich by Friday".

## 6. What does the loot *do*? (the payoff)

Sandbox progression should **widen options, not raise numbers**. Avoid "+5% damage".

- **Cash -> tools**: lockpicks, better picks, a crowbar (quiet but slow), a suppressor, a flashlight with a red lens, a jammer, a disguise kit, bolt cutters for fences, a radio for co-op, a bag that fits more but shows on your back.
- **Cash -> property**: buy or squat a base. Furnish it with the existing furniture system, store loot in it, make it a home people fight over.
- **Cash -> influence**: bribe a guard to look away on a given shift; pay a fence to launder a hot item.
- **Items with stories**: item serials already exist. A stolen heirloom stays *that* heirloom, can be recognized by its owner on sight, and is hot until laundered. Provenance as gameplay.
- **Trophy and prestige**: a wall in the hub of notable hauls, with the history of who took it, from where, when. Free, social, and uses existing tracking.
- **Reputation**: the hub remembers you as stealthy, violent, generous. NPC fences and recruiters react. Pure flavour at first, hooks later.

## 7. What keeps risk real? (stakes and consequences)

### 7.1 Heat
- Persistent **city heat** across the session: more police on the street, more locks bought, more Nosy neighbours awake. Pull it back with quiet time or by laying low.
- Heat per *player* (description-based) versus per *city* (alert level). Disguise changes the first but not the second.

### 7.2 The city fights back
- **NPC burglars**: other thieves hit the same houses, so a house you cased may already be broken into, and the police are now on edge. Also a natural target for vigilantes.
- **Residents adapt**: after a break-in a house adds a lock, a dog, a light; a shop hires security; a guard shifts schedules. A slowly hardening city makes later runs harder in readable ways.
- **Neighbourhood watch**: after enough incidents, civilians organize patrols with bats and flashlights.

### 7.3 Getting caught
- **Arrest** -> a short jail sequence with an escape minigame (reuse lockpick/kick), confiscated gear sitting in the evidence room, which becomes a *heist target* for the crew ("break out your friend and get your stuff back").
- **Bail** paid by teammates, cash lost, no death.
- **Hardcore** is already in the game: permanent death keeps its place as the opt-in high-stakes mode.

### 7.4 Cost of violence
- Bodies cause investigation; a killed resident's home stays empty and feeds the rumour mill; a family member might grieve or seek revenge. Violence is *possible* but expensive. That keeps stealth the interesting default.

## 8. Structure options (how sessions are shaped)

| Option | What it is | Pros | Cons |
|---|---|---|---|
| **A. Pure sandbox** | Spawn in the city, nothing assigned, only the systems | Truest to the pitch, least work | Aimlessness risk is exactly today's problem |
| **B. Sandbox + job board** | The hub (or a fence NPC) offers optional contracts of varied size | Gives every session a starting point; players ignore it when they have their own idea | Needs a contract generator |
| **C. Run-based** | Each city visit is a timed night with a target and an extraction | Strong tension, reuses "new city each visit" and dungeon extraction | Less "live in the world" |
| **D. Persistent city** | One long-running seeded city that changes between sessions | Residents, locks and heat accumulate; stories build | Server persistence and migration cost |

**Recommendation to try first: B on top of C.** A visit is one night with an optional contract and any self-made goals, and extraction is the end. It reuses the dungeon structure (enter, do, escape), and the city already regenerates per visit. Consider D later once the consequence systems (7.2) exist.

## 9. Job board: contract templates

Generated from the city seed, using only existing data (buildings, residents, items):

1. **Retrieve**: take item X from building Y (X visible on a shelf or in a safe).
2. **Ghost**: enter and leave building Y without any resident reaching Suspicious.
3. **Frame**: leave item X at Z's house so police suspect them. (Moral grey; uses witnesses.)
4. **Rescue**: a teammate is in jail (7.3); free them.
5. **Blackout**: kill power to district D for T minutes and don't get seen.
6. **Delivery**: carry a heavy crate from A to B under the two-hand rule.
7. **Swap**: replace a ledger/document with a fake without the owner noticing.
8. **Sabotage**: break a shop's lights and fuse box so tomorrow's takings fall.
9. **Escort / shadow**: follow resident R all day and note where they go (casing contract).
10. **Cleanup**: someone left a body; hide it before the sun comes up (uses drag, hide).
11. **Vigilante**: stop the NPC burglar at street S tonight.
12. **Bounty**: police want player X (a teammate) for a bounty; pick a side. (PvP-optional.)

Each has a base reward, a bonus for style (unseen, no violence, under time, no light turned on), and a difficulty read from its building (locks, residents asleep, guard posted).

## 10. Style as the scoring system

Instead of only money, award **style tags** that reflect how you did it. They can feed reputation, fences and trophies:

- **Ghost** (never seen), **Gentle** (no one hurt), **Loud** (gunfire), **Janitor** (left no trace; doors relocked, bodies hidden), **Blackout** (power cut), **Gossip** (planted a false description), **Faceless** (completed in disguise), **Crew** (used 3+ roles).

This turns the same job into many different games and rewards creativity with no extra content.

## 11. Co-op roles (what each friend can specialize in)

- **Lockman**: picks and breaches. **Lookout**: watches routines and radios. **Mule**: carries the heavy loot. **Face**: the one who talks to residents (distraction, alibi). **Wire-man**: handles the power grid. **Driver/Runner**: gets the crew out. **Cop**: plays the other side.

Mechanics that *make* roles matter: a radio item, a camera feed from a lamp post, tools that only certain players carry, two-person carries, a lock that needs a second person to hold something.

## 12. Unlockable "new game" ideas that reuse the world

Not core loop, but cheap spin-offs the same city could host later:

- **Night shift**: you are the security guard; players try to rob you. Asymmetric.
- **Murder-mystery**: one player commits a quiet crime; others reconstruct it from witnesses and evidence.
- **Property wars**: crews compete to own the most buildings by day's end.
- **Roommates**: a purely cooperative domestic sim in a seeded house (the furniture system is already there).

## 13. Smallest slice worth building first (a proposal)

If we only build a handful of things to make the demo feel like a game:

1. **A value system for loot**: stash contents and cash, scaled by building type and residents.
2. **A hub "fence" with a sell counter** and a short list of buyable tools, so loot becomes capability.
3. **A tiny job board** with 3 templates: Retrieve, Ghost, Blackout.
4. **Heat**: a per-session city alert that hardens the world in 2-3 visible ways.
5. **Arrest -> evidence room -> bail/jailbreak**, so failure gives a story.
6. **Style tags and an end-of-night summary** (what you took, what the city remembers, tags earned). This one screen sells the whole loop.

Everything else (disguise, rumour planting, persistent city, PvP, roles) layers on top without redoing anything.

## 14. Open questions for you

- Is this a **crime** sandbox (heists, heat, police), a **life** sandbox (residents, homes, jobs), or both? The ideas split here.
- Should violence be **discouraged by consequences** or just one valid style?
- Do you want **PvP** (cops and robbers) or strictly co-op against the city?
- **Persistent** city or a **new city per visit** as now? It decides how much "the city remembers" can mean.
- How **dark** a tone? Body disposal and framing people are strong systems but a specific flavour. Mischief and heists can be lighter.
- Is the **dungeon** a separate mode or a place to bring city loot (or vice versa)?
- Target session length: 15 minutes (run-based) or hours (persistent)?

## 15. Parking lot (raw, unfiltered)

- Dogs that bark at night; a dog biscuit item.
- Parties at night in the "nightlife area" (from TODO 0): a crowd to hide in, a distraction, a target.
- Weather: rain muffles footsteps, fog cuts sight range.
- A newspaper in the hub that recaps last night's crimes *from the witnesses' garbled point of view*. Free narrative, uses the gossip system.
- A "most wanted" board with the culprit's description as the city believes it. Players try to match or break it.
- Pawn shops that recognize hot serials.
- Resident favours: a nice player can earn a spare key.
- Garage sale days, markets, festivals: scheduled events that change crowd density.
- Fire as a hazard and a diversion.
- Spectator or "ghost" mode for caught players, who can still ping for the crew.
