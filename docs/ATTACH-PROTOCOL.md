# Brain attach protocol (draft v0)

Status: draft. A first slice is implemented, measured in a sandbox and
playing in one real game, MAG (section 14); most of the vocabulary is still
design only. "BotBrain" is a working name.

Practical guides sit beside this document:
[changing how bots behave](CHANGING-BEHAVIOUR.md),
[adding things bots can do](ADDING-CAPABILITIES.md) and
[adding a game](ADDING-A-GAME.md).

This document defines how a game attaches to the brain. It is the only
interface between the two. If a game can be described with what is in this
document, it needs no brain code of its own.

## 1. Terms

| Term | Meaning |
|---|---|
| Brain | One standalone process that decides what every bot does, in every game. It contains no game names and no game-specific code. |
| Body | The per-game piece that gives the brain eyes and hands in one title. It contains no behaviour. |
| Link | One connection from a body to the brain. |
| World | One live match, inside the brain. All agents in the same match share one world, however many links feed it. |
| Agent | One bot player in a world. |
| Slot | A player seat a body can offer to the brain. |
| Persona | A persistent bot identity (name, traits, habits). Owned by the brain, bound to a slot for one match, reused across games. |
| Human | A real player in the match. The brain never controls one, but needs to know where they are and what they can see. |

There are three kinds of body. The brain cannot tell them apart.

| Kind | How it works | Links | Typical use |
|---|---|---|---|
| Server-side | The match server creates bot players itself and replicates them to real clients. | 1 link, many agents | Titles where we run the match server (MAG) |
| Headless client | A program that speaks the game's in-match protocol with no game running. | 1 link, 1 or many agents | Peer-to-peer titles whose match traffic is decoded |
| Real client | A running copy of the game, read and driven by the shim. | 1 link per agent | Small-lobby PC titles |

## 2. Rules the design holds to

1. **The brain knows no game.** It knows control modes, objective
   primitives, actions and entity kinds (section 6). A game is a combination
   of those, declared in a manifest.
2. **The body knows no behaviour.** It reports what is true and applies what
   it is told. It never decides, delays, randomises or "humanises" anything.
3. **Per-game work is body code and data only.** If a game needs something
   the vocabularies lack, the vocabulary grows by one entry and every later
   game can use it. Entries are never removed or renumbered.
4. **The game never waits for the brain.** The body sends observations and
   applies the most recent action it has. A late brain looks like a slow
   player, not a frozen match.
5. **One unit system.** Metres, seconds, radians. Right-handed, Z up. Yaw 0
   faces +X and grows counter-clockwise seen from above. Pitch is positive
   looking up. The body converts in both directions.
6. **The body sends ground truth; the brain decides what each agent may
   know.** Field of view, line of sight, delay and memory are applied inside
   the brain, the same way for every game.

## 3. Transport and framing

- TCP on loopback by default. The shim runs inside Wine or Proton, where
  named pipes and Unix sockets do not reliably reach the host, and TCP on
  127.0.0.1 does. A body that runs in the same process as the brain may use
  the same framing over an in-memory pipe.
- Every message is `u32 length`, `u8 type`, then the payload. Little-endian.
- The manifest and other one-off messages carry JSON. Per-tick messages
  (`OBSERVE`, `ACT`) are packed binary records in the field order given in
  section 7, with no padding.
- The brain listens. Bodies connect. Remote bodies are allowed but off by
  default.

## 4. Session flow

```
body                                   brain
 │── HELLO (protocol version, manifest) ──▶│
 │◀─ WELCOME (world id, accepted and rejected vocabulary entries) ─│
 │── SEATS (slots on offer) ──────────────▶│
 │◀─ SEAT_TAKE (slot, persona) ────────────│   brain fills a slot
 │── PHASE (lobby) ───────────────────────▶│
 │── CHOICE (loadout? options, deadline) ─▶│
 │◀─ CHOICE_REPLY (option) ────────────────│   sent when the bot "decides"
 │── PHASE (live) ────────────────────────▶│
 │── OBSERVE (tick n) ────────────────────▶│   every tick
 │◀─ ACT (computed from tick n) ───────────│
 │── EVENT (death, objective change, ...) ▶│   as they happen
 │◀─ SEAT_LEAVE (slot) ────────────────────│   bot quits, like a person would
 │── PHASE (post-match) ──────────────────▶│
```

| Message | Direction | Purpose |
|---|---|---|
| `HELLO` | body → brain | Protocol version and the manifest (section 5). |
| `WELCOME` | brain → body | World id. Lists which manifest entries the brain accepted and which it does not support, so the body knows what will be ignored. |
| `SEATS` | body → brain | Slots available now, with team and any restrictions. Re-sent when it changes. |
| `SEAT_TAKE` | brain → body | Bind a persona to a slot: display name, account id, cosmetic preferences. The body makes that player appear. |
| `SEAT_LEAVE` | brain → body | Remove the bot. The body makes it leave the way a player leaves. |
| `SEAT_LEAVE` | body → brain | The body took the seat back (added 2026-11-16, same payload: a person needed the seat). The brain forgets the bot; the body does not reuse the slot. More bots later come as a new `SEATS` offer, which the brain answers at any time. |
| `PHASE` | body → brain | Where the match is, per world or per agent: `lobby`, `loading`, `spawn_select`, `live`, `dead`, `killcam`, `scoreboard`, `post_match`. |
| `CHOICE` | body → brain | A discrete decision outside real-time play: `ready`, `team`, `loadout`, `spawn_point`, `vote`, `rematch`. Carries the options and a deadline. |
| `CHOICE_REPLY` | brain → body | The option picked. The timing is the brain's: the body applies it on receipt. |
| `OBSERVE` | body → brain | One tick of ground truth (section 7.1). |
| `ACT` | brain → body | Controls for every agent on this link (section 7.2). |
| `EVENT` | body → brain | Things that happen at an instant (section 6.7). |
| `RATE` | brain → body | Ask for a lower observation rate or a different fidelity tier for some agents (section 8). |
| `BYE` | either | Orderly close. |
| `OBJECTIVES` | body → brain | The live objectives in full, when the match goes live. Changes after that arrive as `objective` events. |

If a link drops, the body sets its agents' controls to neutral and may remove
them. People disconnect too, so this needs no special handling in the game.

Presence outside matches (bots showing in lobby lists, friends lists and
leaderboards) is between the brain's population director and the lobby
servers. It is a separate interface and not part of this document.

## 5. Manifest

The manifest is what makes a game expressible without brain code. Sent once
in `HELLO`.

| Field | Content |
|---|---|
| `title`, `build` | Opaque strings. The brain uses them only as keys for tuning data, recorded traces and map packages. |
| `body_kind` | `server_side`, `headless` or `real_client`. Informational. |
| `tick_hz` | Rate at which the body can send `OBSERVE` (30 recommended, 10 minimum). |
| `truth` | `full` if this link sees the whole match, `partial` if it only sees what one client was told. |
| `control_modes` | Which modes exist in this title and, for each, its axes, actions and tuning (section 6.1, 6.2). The look tuning gives the stick response: `max_turn_deg_s`, `max_pitch_deg_s`, `deadzone` and `curve_exponent`, where turn rate = max × ((deflection − deadzone) / (1 − deadzone)) ^ exponent. |
| `look_output` | `stick`: the brain sends stick deflection and the game turns the view. `angles`: the brain integrates the declared turn curve itself and sends yaw and pitch. |
| `teams` | Team ids and, per mode, which teams are hostile to which. Not assumed to be two. |
| `items` | Weapons and tools as data (section 6.4). |
| `vehicles` | Vehicle classes, seats and roles as data. |
| `abilities` | Targeted or timed abilities as data (section 6.5). |
| `objectives` | Which objective primitives this mode uses (section 6.3). The live objects arrive in `OBSERVE`. |
| `identity_hidden` | True when players cannot be told from non-player characters by looking (section 6.6). |
| `map` | Map id and content hash. The brain loads the matching map package (section 9) or reports that it has none. |
| `capabilities` | Optional things this body can do: `coarse_move`, `abstract_combat`, `los_hint`, `spawn_choice`, `mid_match_join`. |

Example, trimmed (values are illustrative, not measured):

```json
{
  "title": "example-shooter", "build": "1.05",
  "body_kind": "real_client", "tick_hz": 30, "truth": "partial",
  "look_output": "stick",
  "control_modes": {
    "foot_shooter": {
      "actions": ["fire", "aim", "reload", "jump", "crouch", "sprint",
                  "melee", "interact", "swap_weapon", "throw", "ability_1"],
      "look": { "max_turn_deg_s": 220, "aim_scale": 0.55, "aim_assist": true },
      "move": { "walk_m_s": 4.2, "sprint_m_s": 6.5, "crouch_m_s": 2.0 }
    }
  },
  "teams": { "ids": [0, 1], "hostile": [[0, 1]] },
  "items": [
    { "id": 12, "class": "rifle", "fire": "auto", "mag": 30, "reload_s": 2.4,
      "range_m": [5, 60], "projectile_m_s": 0 }
  ],
  "objectives": ["hold_zone", "score_race"],
  "identity_hidden": false,
  "map": { "id": "mp_hub", "hash": "..." },
  "capabilities": ["los_hint"]
}
```

## 6. Vocabularies

### 6.1 Control modes

A control mode is how an agent is steering right now. An agent has exactly
one at a time and changes it when the body says so (entering a vehicle,
mounting a gun). Each mode has its own motor profile and skills inside the
brain.

| Mode | Axes | Notes |
|---|---|---|
| `foot_shooter` | move x, move y, look x, look y | First- or third-person shooting on foot. |
| `foot_melee_stealth` | move x, move y, look x, look y | Third-person, targets chosen by lock-on, climbing, crowds. |
| `drive` | steer, throttle, brake | Ground vehicles. Optional look axes for a free camera. |
| `turret` | look x, look y | Fixed or vehicle-mounted gun. |
| `bike_2d` | lean, throttle, brake | Reserved (Trials). Not specified in v0. |
| `fly` | Reserved | Not specified in v0. |

### 6.2 Actions

Actions are named, not buttons. The manifest lists which exist in each mode,
and the body maps them to pad buttons or applies them directly.

`fire`, `aim`, `reload`, `jump`, `crouch`, `prone`, `sprint`, `melee`,
`interact`, `swap_weapon`, `throw`, `use_item`, `handbrake`, `boost`, `horn`,
`enter_exit`, `lock_target`, `high_profile`, `ability_1` … `ability_8`,
`taunt`.

Each tick an action is either held or not. The brain produces the press and
release timing. An action that needs a target (an ability aimed at an entity
or a point) is sent as a targeted command beside the held set.

### 6.3 Objective primitives

Game modes are built from these. Each live objective names its primitive,
which teams attack or defend it, its state, and which other objectives must
finish first.

| Primitive | Meaning | Parameters |
|---|---|---|
| `score_race` | First side to a score, or highest when time ends. | target score, time limit |
| `hold_zone` | Be inside an area to earn or deny. | shape; fixed position or attached to a moving entity |
| `interact` | Stand at a thing and hold an action for a time. | duration, interruptible, repeatable, reversible by the other side |
| `carry` | Take an item or vehicle and bring it somewhere, or just keep it. | carried entity, destination (optional) |
| `eliminate` | Kill or destroy a specific entity. | target entity, assigned to which agent or team |
| `protect` | Keep a specific entity alive. | protected entity |
| `checkpoints` | Pass points in order. | ordered list, laps |
| `survive` | Stay alive or uncaught for a time. | duration |

**`interact`, as built (2026-11-17).** The brain reads three things and
nothing else. `owner_team` is the team whose it is *now* (255 for
nobody's). `progress` says how firmly that owner holds it: 1 when
settled, below 1 while a claim is still running down and the other side
can undo it (a hack holding, a charge armed). `requires` lists the
objectives a team must hold, settled, before it can work this one.
`radius` is how close a body must stand to work it. `locked` (added
2026-11-17 with the owner's agreement; absent means false) says nobody can
work it any more, whoever holds it. The `objective` event carries the same
owner and progress, and the lock as bit 0 of its C field
(`ObjectiveFlags.Locked`; a body that sends 0 means not locked). Which button press does what
(start, undo, retake) is the body's business: the brain holds `interact`
at the objective and watches for the owner or progress to change. MAG's
mapping from its objective states is in `Plugin.MagGameServer/Battle/BotObjectives.cs`.

### 6.4 Entities, items and vehicles

Entity kinds: `player`, `npc`, `vehicle`, `turret`, `projectile`,
`deployable`, `pickup`, `structure`, `objective_marker`.

Items are described by class and numbers so the brain can reason about any
weapon without knowing its name: class (`rifle`, `smg`, `shotgun`, `sniper`,
`lmg`, `pistol`, `launcher`, `melee`, `thrown`, `heal_tool`, `repair_tool`,
`deployable`), fire mode, magazine size, reload time, useful range band,
projectile speed (0 for instant), blast radius.

Vehicles: class, top speed, and seats with a role each (`driver`, `gunner`,
`passenger`). Taking a seat changes the agent's control mode to the one the
seat declares.

### 6.5 Abilities

Each ability has an effect class (`area_denial`, `self_buff`, `disguise`,
`reveal`, `heal`, `strike`, `possess`, `order`), a target type (`none`,
`self`, `entity`, `point`), a cooldown and a range. `possess` moves the
agent's control to another entity. `order` is a command given to other
players and arrives at them as an event.

### 6.6 Hidden identity and hints

Two things the brain must be able to *not* know:

- **Hidden identity.** When the manifest sets `identity_hidden`, every
  character carries an appearance id, and the body still reports which ones
  are players. The brain's perception withholds that fact from each agent
  and lets it be inferred from behaviour (running, climbing, changing
  direction oddly).
- **Hints.** Some games tell a player roughly where something is: a compass
  toward a target, a radar ping, a marker placed by a teammate. A hint is a
  bearing or position with an uncertainty, optionally tied to an entity.
  Hints are the only way the brain learns anything the agent cannot see.

### 6.7 Events

`damage` (victim, attacker if known, direction, amount), `death` (victim,
killer, item), `shot` (entity, position), `explosion` (position, radius),
`objective` (id, new state, progress, owning team, flags: locked), `score` (team, value),
`order` (issuer, objective or point), `hint` (section 6.6), `revive`,
`seat_change`, `control_mode` (agent, new mode, new controlled entity).

The brain derives footsteps and other continuous sound from entity speed and
stance. The body does not send them.

## 7. Per-tick records

### 7.1 `OBSERVE`

One message per link per tick. Three lists.

**Entities** (everything this link knows about, sent once, not per agent):

| Field | Type | Notes |
|---|---|---|
| id | u32 | For players, the match-wide player id, so the brain can merge reports from several partial links. |
| kind | u8 | Section 6.4. |
| team | u8 | 255 for none. |
| flags | u16 | alive, human, downed, crouched, prone, aiming, firing, sprinting, airborne, in_seat. |
| position | 3 × f32 | |
| velocity | 3 × f32 | |
| yaw, pitch | 2 × f32 | Where it is looking. |
| appearance | u16 | Character class or model. |
| held_item | u16 | Index into the manifest's item table. |
| health | u8 | 0 to 255 as a fraction, 255 if unknown. |
| parent, seat | u32, u8 | Vehicle or turret occupied, if any. |

**Agents** (private state for each agent on this link):

| Field | Type | Notes |
|---|---|---|
| slot | u16 | |
| entity | u32 | Which entity it controls right now. Changes on `possess` or seat change. |
| control_mode | u8 | |
| ammo_in_mag, ammo_reserve | u16, u16 | |
| cooldowns | 8 × u8 | Remaining, in tenths of a second, per ability. |
| lock_target | u32 | Entity the game has locked onto, if the mode has lock-on. |
| los_bits | optional | With `los_hint`: which entities the game itself considers visible to this agent. |

**Observers** (one per human): camera position, yaw, pitch, field of view,
and which entity they are spectating or watching in a killcam. The brain uses
this only to decide fidelity (section 8).

Objectives are sent in full when the match goes live and after that only
through `objective` events.

### 7.2 `ACT`

One message per link, sent in answer to every `OBSERVE` and carrying that
observation's tick, then a record for each agent whose controls changed. An
agent at a lower fidelity tier is not in every message; the body keeps its
last record.

| Field | Type | Notes |
|---|---|---|
| slot | u16 | |
| from_tick | u32 | The observation this was computed from. |
| axes | 4 × i16 | Meaning depends on control mode. Normalised to ±32767. With `look_output: angles`, the look axes carry absolute yaw and pitch instead. |
| held | u32 | Bit set of held actions, in the order the manifest listed them for the mode. |
| command | optional | Ability or action id plus a target entity or point. |

The body applies the newest record it has. If none arrives for 250 ms it
sets axes to neutral and releases all actions.

## 8. Fidelity tiers

The brain decides how much effort each agent gets from whether a human can
see it, is near it, is being targeted by it, or is spectating it.

| Tier | When | What the brain sends | Needs |
|---|---|---|---|
| Full | A human can observe the agent now or within a few seconds | `ACT` at 30 Hz with the full motor model | nothing |
| Reduced | Humans nearby but not looking | `ACT` at 10 Hz, simplified motor | nothing |
| Coarse | No human can observe it | A route and a speed; fights against other coarse agents are resolved by the brain and reported as damage | `coarse_move`, `abstract_combat` |

A body without the coarse capabilities keeps every agent at Full or Reduced.
That is fine for small lobbies and is the case for every real-client body.

Target budget: 256 agents in one world on one or two desktop cores. The
sandbox measured about 4% of one laptop core with every agent at Full
(section 14), on a map far simpler than a real level.

## 9. Map package

One per map, produced offline by a per-game export tool, stored by the brain
and found by the id and hash in the manifest.

- Navigation graph with typed links: `walk`, `jump`, `drop`, `climb`,
  `ladder`, `vault`, `swim`, `drive`.
- Collision mesh, simplified, for line-of-sight rays.
- Visibility cells: which regions can see which.
- Annotated points: spawns, cover, objective locations, vehicle spawns.
- Optional, added over time: routes and positions taken from recorded human
  play.

With no package the brain still runs: it relies on `los_hint` if offered and
builds walkable space from where it has seen players go. Behaviour is worse
until a package exists.

### 9.1 What the package is today (version 2)

The list above is the design. What is built is smaller:

- A grid of square cells (0.5 m for the MAG levels) with a world origin.
- Per cell: walkable or not, the ground height, and eight link bits, one per
  neighbour. A link can be one-way, which is how a drop off a ledge is
  stored. Route search and flow fields follow the links, so a bot will jump
  down a platform and will not try to walk back up it.
- Optionally the level's triangles, bucketed on the same ground plane, for
  sight lines and shots in three dimensions.
- Named points: spawns per team, and points of interest that bots roam
  between when the match gives them no objective.

It holds one walkable layer. Upper floors and anything under a roof that is
also walkable on top are not represented. The file is `<id>-<sha256>.bbmap`.

`BotBrain.MapTool build <mesh.soup> <points.json> <out dir>` makes a package
from a triangle soup and a list of spawn points. It flood-fills from the
spawns: a step is allowed when the ground rises or falls by at most 0.45 m
and probes at knee, waist and chest height, also 0.25 m to each side, hit
nothing. A fall of up to 4 m becomes a one-way link. Getting the soup out of
a game is that game's export tool; the brain's side never sees the game's
own formats.

## 10. Body kit

Server-side and headless bodies all need the same three things that a real
client gets from the game for free:

1. Turn stick input into movement that stays on the ground and out of walls.
2. Turn a held `fire` into shots that hit or miss against level geometry.
3. Hold per-agent state (health, ammunition, reload timers).

These go in one shared library that reads the same map package as the brain,
so each such body only has to translate between the kit and its game's wire
format. The kit is not part of the brain and makes no decisions.

## 11. Paper check against five titles

Each title is written out in protocol terms. Mode lists are from memory and
should be confirmed against each game before its body is built.

### MAG (PS3)

| Aspect | Expression |
|---|---|
| Body | Server-side, inside `Plugin.MagGameServer`. One link, up to 255 agents. |
| Control modes | `foot_shooter`, `drive`, `turret`. |
| Teams | Two, except Escalation with three. |
| Suppression | `score_race`. |
| Sabotage | Two `interact` objectives, then a third that requires both. |
| Acquisition | `carry` where the carried entity is a vehicle, with a destination. |
| Domination | A chain of `interact` objectives with prerequisites, plus `eliminate` on support structures and `interact` to repair them. |
| Interdiction | `hold_zone` × 3 with a time limit. |
| Squad and platoon orders | `order` ability for leaders, `order` event for members. |
| Leader support calls | Abilities with effect `strike` or `reveal`, target `point`. |
| Revive, heal, repair | Items of class `heal_tool` and `repair_tool`. |
| Spawn selection | `CHOICE` of kind `spawn_point`. |
| Scale | Needs `coarse_move` and `abstract_combat`. |

Finding: in this server, clients own their characters. A client sends its
own position, yaw and pitch, and reports its own hits (`ClientUpdate`,
`NetHit`). The server does not move characters or trace shots. A bot here is
therefore a character whose fields the server writes itself, so the body has
to supply movement and shot tracing. The plugin's `Data` folder holds spawn
points and level objects but no level collision data (checked by listing the
folder only, not by reading every source file). MAG is the first customer
for the body kit and for a map export tool.

### Far Cry 3 (PC)

| Aspect | Expression |
|---|---|
| Body | Real client through the shim. One link per bot, `truth: partial`. |
| Control modes | `foot_shooter`. |
| Team Deathmatch | `score_race`. |
| Domination | `hold_zone` × 3. |
| Transmission | `hold_zone` whose active zones change, sent as `objective` events. |
| Firestorm | Two `interact`, then a `hold_zone` that requires both, reversible by the other side. |
| Revive | `interact` on a downed teammate (`downed` flag). |
| Battle cry | Ability, effect `self_buff`. |
| Lobby | `CHOICE`: `ready`, `loadout`, `vote`. |

Finding: nothing new needed. Merging partial views by player id is what
makes a full lobby of shim bodies behave as one world.

### Driver: San Francisco (PC)

| Aspect | Expression |
|---|---|
| Body | Real client through the shim. |
| Control modes | `drive`. |
| Races | `checkpoints`. |
| Tag | `carry` with no destination: hold the item as long as possible. |
| Trailblazer | `hold_zone` attached to a moving entity. |
| Takedown | `eliminate` for one side, `survive` for the other. |
| Shift | Ability with effect `possess`, target `entity`. The agent's controlled entity changes and the body reports it. |

Finding: forced three additions. Zones can be attached to entities, `carry`
can have no destination, and an agent's controlled entity can change
mid-match.

### Assassin's Creed Brotherhood (PC)

| Aspect | Expression |
|---|---|
| Body | Real client through the shim. |
| Control modes | `foot_melee_stealth`. |
| Teams | Free-for-all in Wanted (every player its own team), two teams elsewhere. |
| Wanted | `eliminate` with a target assigned per agent, plus `survive` against unknown pursuers. |
| Manhunt | `eliminate` for the hunting team, `survive` for the hiding team, roles swap by `objective` event. |
| Chest Capture | `interact` and `protect`. |
| Escort | `protect` and `eliminate` on moving non-player characters. |
| Crowds and lookalikes | `identity_hidden`, with `appearance` on every character. |
| Compass and proximity meter | `hint` events with a bearing and uncertainty. |
| Abilities | Effect classes `area_denial`, `disguise`, `self_buff`, `reveal`. |
| Climbing | `climb`, `vault`, `drop` links in the map package. |

Finding: forced hidden identity, hints and typed navigation links. Acting
like a crowd member convincingly is a large piece of brain work inside
`foot_melee_stealth`, but it is not a protocol gap: the body already reports
how crowd characters move, and the brain can copy them.

### Grand Theft Auto IV (PC)

Added 2026-11-16. The interest here is one game whose modes are different
kinds of play: racing, shooting, objective modes and co-operative missions.

| Aspect | Expression |
|---|---|
| Body | Real client. A plug-in inside each bot's copy of the game (the game already loads `gta4-mp-gate` this way) reads players, vehicles and mission state through the game's own script functions and feeds a virtual controller. One link per bot, `truth: partial`. |
| Control modes | `foot_shooter` on foot, `drive` in a driver's seat, `turret` for a passenger shooting from a window. The mode changes every time the character gets in or out, reported per agent per tick. |
| Deathmatch, Team Deathmatch | `score_race`. |
| Race | `checkpoints`, in `drive`. |
| GTA Race | `checkpoints`, with weapons and with getting out allowed: `drive` and `foot_shooter` in one match. |
| Turf War | `hold_zone`. |
| Mafiya Work, Car Jack City | A stream of short jobs sent as they are handed out: `eliminate` a target, `carry` a car or package to a place. |
| Cops n' Crooks | Crooks: `protect` the boss and reach the getaway (`checkpoints` with one point). Cops: `eliminate` the boss. |
| Co-operative missions (Hangman's NOOSE, Deal Breaker, Bomb da Base II) | Every bot on one team. A chain of objectives linked by `requires`: `eliminate` computer-controlled enemies, `protect` a character, `carry` or escort to a place, `interact`. |
| Computer-controlled characters | Entities of kind `npc` with a team: hostile in missions, police hostile to whoever is wanted, bystanders on a neutral team. |
| Free Mode | No objectives. |
| Lobby | `CHOICE`: `ready`, `team`, vehicle for a race. |

Findings:

1. **Nothing new on the wire.** Mixing kinds of play needs no new message
   (see "One match, several kinds of play" below).
2. **The map package cannot hold a city.** Liberty City is several
   kilometres across. A half-metre grid would be over a hundred million
   cells. This game needs a road graph for driving and a navigation mesh
   for walking. The game ships both as its own path data, so the exporter
   reads those instead of flood-filling triangles. This is the largest new
   piece and it is in the map package, not the protocol.
3. **One copy of the game per bot limits the count.** GTA IV is heavy. An
   ordinary PC runs a few copies, not a lobby's worth. That is a property
   of real-client bodies; the only way round it is a program that speaks
   the game's network protocol itself, which is a far larger job than any
   body so far.
4. **Free Mode has no objective**, and a person with nothing to do is the
   hardest thing to imitate. Roaming between points of interest, as on
   foot today, is the starting point.

### One match, several kinds of play

The brain is not built per genre. It is built along two axes that do not
depend on each other, and a game mode is one choice on each:

- **How the character is controlled right now**: the control mode, per
  agent, per tick. It selects the model of the hands and the movement
  skills.
- **What there is to achieve right now**: the live objectives, per match,
  changing as the match goes. They select the goals.

| | `score_race` | `hold_zone` | `checkpoints` | `eliminate` / `protect` / `carry` / `interact` chain |
|---|---|---|---|---|
| `foot_shooter` | deathmatch | zone modes | a foot race | an objective mode, a co-op mission on foot |
| `drive` | vehicle combat | a moving zone to stay near | a race | a getaway, a delivery, an escort |
| both, switching | deathmatch with cars | | a race you may leave your car in | a mission: drive there, fight, drive back |

So a GTA IV bot is not three bots. It is one agent whose control mode
changes when it gets into a car and whose objectives are whatever the mode
has sent. The same persona carries across: someone reckless on foot is
reckless at the wheel.

What this needs in the brain that is not built, in the order to build it:

1. Read each agent's control mode every tick and pick skills and hands by
   it. (Today the brain reads only `foot_shooter` and refuses a game
   without it.)
2. The `drive` mode, proved in the sandbox on a track first: a road graph
   in the map package, the `checkpoints` objective, a driving skill, and a
   model of steering and pedals. Shared with Driver: San Francisco.
3. Deciding to get in and out, and which seat.
4. The remaining objective kinds: `eliminate`, `carry`, `protect`,
   `interact`.
5. Computer-controlled characters as things to fight, guard or ignore.
6. One world from several links, each bot's game seeing only part of the
   match. Shared with every real-client game, Far Cry 3 first.
7. The city-scale map.

Items 1, 4 and 6 are also needed by MAG's objective modes or by Far Cry 3,
so they come first whatever the order of games.

### Result

All five are expressible with no title-specific message or field. Grand
Theft Auto IV added nothing to the wire and one requirement to the map
package (a road graph and a navigation mesh in place of a grid for large
maps). The first four added these to the draft:

1. Teams are ids with a hostility table, not "us and them".
2. `hold_zone` can follow an entity; `carry` can lack a destination.
3. The entity an agent controls can change (`possess`, vehicle seats).
4. Hidden identity and hints.
5. Typed navigation links.
6. Spawn choice as a `CHOICE`.
7. The body kit, because a match server may not simulate movement at all.

## 12. Open decisions

1. **Brain language.** C# fits the existing servers and lets a server-side
   body run the brain in the same process later as an optimisation. The
   protocol does not depend on the choice.
2. **Sandbox first.** Done; see section 14.
3. **Coarse tier hand-off.** How an agent moves from Coarse to Full without
   a visible snap when a human turns toward it. Needs a rule for how far
   ahead "about to be observed" looks.
4. **Scoring style.** Some games reward how a kill is made, not just that
   it happened. Whether that is an objective parameter or per-title tuning
   data is undecided.
5. **Map export.** One tool per engine. Order of work follows the order of
   titles.
6. **Recording.** Matches are now recorded from the brain's side (section
   14.5), in a format of their own, not as raw protocol frames. Still
   open: recording a person's real pad input from their client, which is
   the data for tuning the hands against.

## 13. Prior work to build from

Surveyed 2026-11-15. "Checked" means the fact was confirmed by a search that
day. "From memory" means it was not re-checked and should be before relying
on it. No published project covers the whole brain; these are parts.

### 13.1 Libraries to use directly

| Library | Use | Licence | Status |
|---|---|---|---|
| [DotRecast](https://github.com/ikpil/DotRecast) | Navigation mesh building and path queries in C#. A port of Recast/Detour. | zlib | Checked. Releases into 2026. |
| [Recast/Detour](https://github.com/recastnavigation/recastnavigation) | The C++ original, for the map export tools if they are not C#. | zlib | From memory. |
| [Fluid HTN](https://github.com/ptrefall/fluid-hierarchical-task-network) | Hierarchical task planner in C#, for squad and individual plans. | MIT | Checked (README badge). |
| [BepuPhysics v2](https://github.com/bepu/bepuphysics2) | Fast rays against level meshes in C#, for line of sight and the body kit's shots. | Apache 2.0 | From memory. |

### 13.2 Designs to port

| Source | What to take | Licence | Status |
|---|---|---|---|
| Killzone 2 and 3 multiplayer bots: [Verweij's thesis](https://www.guerrilla-games.com/read/a-hierarchically-layered-multiplayer-bot-system-for-a-first-person-shooter), [2009 Paris slides](https://www.slideshare.net/guerrillagames/killzone-2-multiplayer-bots), Game AI Pro chapter 29 | Commander, squad leader and individual layers, with orders passed down and a task planner per bot. Shipped with 14 bots per side, so it shows structure, not scale. | Papers only | Checked. |
| Quake III Arena bot: [van Waveren's thesis](https://www.cs.rochester.edu/u/brown/242/docs/QuakeIII.pdf), [code review](https://fabiensanglard.net/quake3/a.i.php), source in [ioquake3](https://github.com/ioquake/ioq3) | Per-bot character files of numeric traits (reaction time, aim accuracy, aggression, camping and others) as the starting persona vocabulary; goal selection by fuzzy weights. | GPL (C source) | Thesis checked. Trait names and botlib licence from memory. |
| [UT^2](http://nn.cs.utexas.edu/?ut2) (BotPrize 2012 co-winner) | Replay of recorded human traces for movement, combined with tuned combat behaviour. Judged human 52% of the time against 40% for real humans. | Source available, licence not checked | Result checked. |
| MirrorBot (BotPrize 2012 co-winner) | Briefly mirroring a nearby non-threatening player's movement. | Paper only | Checked. |
| [Game AI Pro](https://www.gameaipro.com/) (all chapters free) | Tactical position selection, influence maps, perception systems, racing lines. Game AI Pro 3 chapter 17 covers Driver San Francisco's own AI. | Papers only | Checked. |
| TORCS and Speed Dreams driver robots | Reference racing drivers for the `drive` mode. | GPL | From memory. |

### 13.3 Learned approaches, for later

These need recorded play we do not have yet. The protocol already carries
what they need, so a hand-built skill can be replaced by a learned one
without changing a body.

| Source | Relevance | Status |
|---|---|---|
| [Human-like Bots for Tactical Shooters Using Compute-Efficient Sensors](https://arxiv.org/abs/2501.00078) (Riot Games, modl.ai, 2025) | Imitation learning from 48.6 hours of play by 28 people, ray-cast sensors instead of pixels, inference on modest CPUs. About a third of participants took the bots for humans. Closest match to our observation design. | Checked. |
| [Learning to Move Like Professional Counter-Strike Players](https://arxiv.org/abs/2408.13934) (MLMove, 2024), code at [mlmove.github.io](https://mlmove.github.io) | Learned movement model beside a rule-based module for everything else. 123 hours of data for one map and one mode. | Checked. |
| [Counter-Strike Deathmatch with Large-Scale Behavioural Cloning](https://arxiv.org/abs/2104.04258) (2021), [code](https://github.com/TeaPearce/Counter-Strike_Behavioural_Cloning) | Pixels to actions. Not our route, useful as a baseline. | Paper checked, licence not confirmed. |
| [NitroGen](https://arxiv.org/abs/2601.02427) (NVIDIA, 2026), [code](https://github.com/MineDojo/NitroGen) | Open pixels-to-gamepad model across 1,000+ games. Needs rendered frames per bot, so it does not fit the 256-agent budget. Non-commercial licence. | Checked. |

### 13.4 Measuring human-likeness

| Source | Use | Status |
|---|---|---|
| [Navigation Turing Test](https://arxiv.org/abs/2105.09637) (Microsoft, ICML 2021), [code](https://github.com/microsoft/NTT) | Train a classifier on human and bot traces to predict which is which, and use what it keys on as the list of tells to fix. | Checked. |

### 13.5 Motor model

| Source | Use | Status |
|---|---|---|
| Do, Chang and Lee, "A Simulation Model of Intermittently Controlled Point-and-Click Behaviour", CHI 2021 ([DOI](https://doi.org/10.1145/3411764.3445514)) | Aiming at still and moving targets as intermittent control with perception, click timing and device effects. | Checked. |
| Martín, Gollee, Müller and Murray-Smith, ["Intermittent control as a model of mouse movements"](https://arxiv.org/abs/2103.08558), 2021 | Evidence that corrections happen in bursts, not continuously. | Checked. |
| Harris and Wolpert 1998 (noise grows with movement speed); Meyer et al. 1988 (aiming as a first move plus corrections) | The two classic results behind overshoot-and-correct aiming. | From memory. |

### 13.6 Reference only until the licence is known

| Source | Why it is interesting | Status |
|---|---|---|
| [fun-bots](https://github.com/Joe91/fun-bots) (Battlefield 3 on Venice Unleashed) | Community bots for a revived game that follow paths recorded from players and drive vehicles. Closest project in spirit. | No licence visible on the repository page. |

### 13.7 Not found

1. **Thumbstick aiming.** The motor studies above are about mice. Aiming
   with a pad and aim assist will be fitted to our own recordings.
2. **Population and personas across games.** Nothing on persistent
   identities, schedules and quitting behaviour shared between titles.
3. **Scale.** Nothing on 256 agents with fidelity tied to what humans can
   see.

### 13.8 Licence note

If GPL code (for example a port of the Quake III bot) goes into the brain,
the brain is GPL. Bodies talk to it over a socket and are separate programs,
so the server plugins and the shim are generally understood to keep their
own licences. This is the common reading, not legal advice.

## 14. Sandbox results

Measured 2026-11-15 on a 12th Gen Intel Core i7-1260P laptop, .NET 10,
Release build, brain on one thread.

### 14.1 What was run

A sandbox world stands in for a game: a generated 512 m square town on a
1 m grid, two teams, three zones to hold, one rifle. It is a server-side
body speaking this protocol over TCP on loopback. Each match is ten
simulated minutes at 30 Hz with 256 players.

| Run | Bots | Stand-in humans | Fidelity | Brain per tick, mean | 99th percentile | Share of one core |
|---|---|---|---|---|---|---|
| A | 255 | 1 | all Full (worst case) | 1.23 ms | 2.34 ms | 3.7% |
| B | 255 | 1 | tiered | 0.80 ms | 1.64 ms | 2.4% |
| C | 248 | 8 | tiered | 1.03 ms | 2.05 ms | 3.1% |
| E | 255 | 1 | all Full, brain as a separate process, real-time pacing, 60 s | 1.40 ms | 3.20 ms | 4.2% |

The tick budget at 30 Hz is 33.3 ms. Peak memory for brain and sandbox
together in one process was 67 MB.

In run A the time per tick went: perception 0.63 ms, skills and motor
0.40 ms, decisions 0.08 ms, encoding and decoding 0.07 ms, taking in the
observation 0.04 ms, events 0.01 ms. The brain cast about 30,000 rays a
second, for sight and for steering.

### 14.2 What the numbers do and do not show

- **The brain is not the constraint.** 256 agents at full fidelity cost a
  few percent of one core. The cost of a title will be decided by its body.
- **Rays here are cheap.** A grid ray costs well under a microsecond. Rays
  against a real level mesh will cost several times more. At 30,000 a
  second that is still a small share of a core, but it has not been
  measured.
- **Tiering saves about a third**, not more, because only Full and Reduced
  exist. The Coarse tier is not built.
- **The first tick after the process starts takes 30 to 45 ms**, once,
  while code is compiled. In the real-time run the bots' controls were late
  for the first 19 ticks (0.6 s) and never after.
- **One setting matters.** With the .NET default the first route search of
  a match ran in unoptimised code and took 30 to 40 ms mid-match.
  `TieredCompilationQuickJitForLoops=false` removes that. Any process that
  hosts the brain needs the same setting.
- **Nothing here measures human-likeness.** The bots cross the map, notice
  enemies after a reaction time, aim through a thumb model, fight, and
  take zones. Hit rate was 30%, which is probably too high for pad players
  at these ranges and has not been tuned against any recording.

### 14.3 What is implemented

| Area | State |
|---|---|
| Framing, `HELLO`, `WELCOME`, `SEATS`, `SEAT_TAKE`, `OBJECTIVES`, `OBSERVE`, `ACT`, `EVENT`, `BYE` | Implemented. |
| `SEAT_LEAVE` | Body → brain acted on (the bot is removed). Brain → body never sent. |
| `CHOICE`, `CHOICE_REPLY`, `RATE`, `PHASE` handling, targeted commands | Defined, not acted on. |
| Control modes | `foot_shooter` only. |
| Objective primitives | `hold_zone`; `interact` with roles (section 14.8); `score_race` is accepted and ignored. |
| Perception | Field of view (about 80 degrees), line of sight against the collision mesh only (bushes and the like do not hide anyone), reaction delay that grows with distance, angle off centre and stillness, four tracked enemies, memory that fades, alerts from being hit and from nearby gunfire, distant gunfire out to 160 m, a team's shared memory of where it met the enemy (contacts). |
| Decisions | Fight, chase a remembered enemy, hunt (back to its killer, toward distant gunfire, by persona, some round the side), camp at a vantage point (by persona), go to a zone, hold a zone, roam toward the fight when there is no objective. Squads of eight spread over zones once a second. With `interact` objectives: a role per bot (seeker, defender, attacker), an objective per bot, working it (holding `interact`), guarding a teammate at work, keeping a post round a defended one (14.8). |
| Motor | Aim re-planned from a noisy error estimate, thumb as a damped spring, tremor, anticipation of moving targets, trigger delay, bursts. |
| Personas | Generated from a seed. No persistence, no schedules, no mood. |
| Fidelity | Full and Reduced. No Coarse. |
| Worlds | One link per world. Merging several partial links into one world is not built. |
| Maps | Grid package with heights, one-way links and a triangle mesh for sight (section 9.1). One walkable layer. Built from a triangle soup by `BotBrain.MapTool`. |
| Bodies | The sandbox, and MAG 2.12 inside its game server (section 14.4). |
| Recording | Every match can be written to a file and replayed in `viewer/index.html` (section 14.5), including each team's contacts, each hunt (14.6) and each camp (14.7). |
| Hidden identity, hints, vehicles, abilities, lobby choices | Not built. |

### 14.4 First real level and first real body (2026-11-15)

**A real level, offline.** MAG's SVER Suppression level was exported from
the disc's collision data (2.4 million triangles before the play area is
cut out) and built into a package of about 7 MB. The sandbox then ran a
64-player match on it for five simulated minutes: 182 kills, the brain at
about 4.5% of one core, perception about 1.2 ms a tick. Rays against the
real mesh cost roughly four times what grid rays did, as section 14.2
expected, and the total stays small.

Two faults showed up only on real geometry and are fixed:

- Bots got stuck constantly (9,897 recoveries in one match). The map
  builder treated a bot as a point while movement gave it a 0.35 m radius.
  The builder now probes to each side and built maps use a 0.1 m body
  radius. 59 recoveries after.
- With no objective, bots walked to the squad's roam point and stood there
  (43% of bot time spent still). Each bot now picks its own next point on
  arrival. 11% after.

**MAG body.** MAG's clients own their characters and report their own
hits, so the body lives in the game server: a bot is a seat with no
connection whose character the server moves, replicates and damages. It
runs the brain in the same process over loopback TCP, steps the bots at
30 Hz on the map package, traces their shots against the level mesh, and
sends each bot's position, facing, stance and held weapon to the clients
three times in ten ticks, which is the rate real clients send at.

An automated test with a test client covers seating, spawning, movement and
damage both ways. **It has not been played by a person yet.** Not known
until it is: whether the real client accepts characters that have no
client behind them, whether pitch is the right way up, and whether the
held-weapon fields are enough for other players to see a bot firing.

**What the MAG body does not do:** grenades, vehicles, reviving, crouching
or going prone, colliding with other characters, capturing objectives
(Suppression has none). Bots hear a person's gun only when a shot of
theirs hits something the server is told about. Bot weapons fire ten
rounds a second from a 30-round magazine whatever the gun.

**Any host needs the code optimised from first use.** The MAG game server
does not have `TieredCompilationQuickJitForLoops=false`, so route search,
flow fields and the ray loops are marked `AggressiveOptimization` and no
longer depend on the host's setting.

### 14.5 Recording and the replay viewer (2026-11-16)

MAG's bots were played against by a person for the first time on this
date: the client accepted them and they fought. The person could not tell
from inside the match whether they moved like people. That is what the
recording is for.

**What is recorded.** With `BrainOptions.RecordDirectory` set, the brain
writes one `.bbrec` file per match, from its own side of the protocol, so
every body's matches record the same way:

- each frame, every entity the body reported: position, facing, flags,
  health;
- each frame, for every bot: goal, fidelity tier, target, the four stick
  axes and held buttons as sent, the point it is steering at, where it is
  headed, the point it is trying to look at, and each enemy it is tracking
  (noticing, visible or remembered, and where it thinks they are);
- every event batch as it arrived;
- once: the manifest's look and move settings, each bot's persona, the
  objectives, and the map seen from above.

The layout is in `BotBrain.Core/World.Recording.cs`. Every tick is recorded
up to 64 players and every third tick above that
(`BrainOptions.RecordEveryTicks` overrides). A 3-minute match of 32 players
at 30 frames a second came to 4.5 MB and cost 0.05 ms a tick.

**What the viewer shows.** `viewer/index.html` is one file with no
dependencies. It draws the match from above with a timeline of kills, and
for a selected player a trail, the turn rate and speed around the current
moment, and how their turn rates are distributed over the whole match next
to another player's or all the people's. For a bot it adds the destination,
the tracked enemies, the stick positions, the turn rate the stick asked for
against the turn rate that happened, and the goal over time.

**What it cannot show yet.** People have no names in the protocol, so they
appear as numbered players. A person's sticks are unknown; only what their
character did is. A body that reports people at a lower rate than its tick
(MAG's clients send ten updates a second) gives stepped curves for them;
the chart's "measured over" setting smooths that.

**First thing it showed.** In the sandbox, a bot acquiring a target goes
from no turn to the body's top turn rate and stays pinned there until it
arrives. Nothing has been changed in response yet.

### 14.6 Roaming toward the fight, and hunting (2026-11-16)

The owner, watching a recording: bots "constantly sweeping the back where
there is nothing". Measured on the sandbox sample: 10 of 17 roam points sat
on the rim of the play area (the 130 m cut around the spawn line, not the
level's real edge), 65% of roam trips went there, bots roamed 91% of the
time and had no enemy within 60 m 83% of the time.

The owner added what real players do: go back for whoever killed them,
head for gunfire, or flank.

Changed: the map tool places roam points only on the ways between the two
sides; the brain weights its choice by route, front line and the team's
recent contacts; and bots hunt (revenge, gunfire, some round the side).
Details and numbers to tune are in CHANGING-BEHAVIOUR.md 2.1 to 2.4 and 2.8.
Following a route search was also fixed: a bot that cut a corner kept
steering at a waypoint behind it and stuck there (179 stuck recoveries in
hunts before the fix, 5 after).

Sandbox, SVER, 31 bots, 180 s, seed 1:

| | Before | After |
|---|---|---|
| Kills | 50 | 138 |
| Time more than 80 m off the spawn line | 27% | 0.1% |
| Time with no enemy within 60 m | 83% | 47% |
| Fighting / roaming / hunting | 5% / 91% / 0 | 14% / 32% / 47% |
| Standing still | 9% | 3% |
| Stuck recoveries | 28 | 18 |
| Brain, mean / p99 per tick | 0.40 / 3.1 ms | 1.2 / 10 ms |

The extra cost is perception: bots now meet, so more of them have enemies
in view to cast rays at. A one-off perception spike of 60 to 76 ms showed
up at a different tick on each run of the same match (and not on every
run), so it is the machine (collection or compilation), not the logic, but
it is worth watching at 256 players.

Recording: block 5 holds each team's contacts when they change (at most
once a second) and block 6 each hunt as it starts. Version stays 1: readers
skip blocks they do not know. The viewer draws contacts as fading crosses
in the knowing team's colour (with "Map points"), names the hunt in the
bot's panel ("going back for its killer, round the side") and draws the
flank route.

**Not known until played:** whether it looks like people. Hunting is 47% of
bot time in the sandbox, which may be too much once real people are in the
match.

**First real match (owner, Valour, 11 min, 4 a side, 2026-11-16).** Bots
kept off the back: after the first 15 s of a life, 0 to 9% of their time
in the rear fifth of their own side. 35 hunts, 28 of them ending in a
fight. Hunting was 27% of bot time (roaming 55%). What still gave them
away, against the owner in the same match: bots stood still 0 to 4% of the
time where the owner did 20%, sprinted 70 to 81% against 40%, paused 3 to
9 times in 11 minutes against 95, and turned up to about 210°/s against
141. Not changed yet.

### 14.7 Human eyesight and campers (2026-11-16)

The owner: bots "got too good of vision", seeing too far and too wide; and
some bots should camp at points with good sight lines.

**Eyesight, measured in that match:** half of the bots' first sightings
were beyond 55 m, a quarter beyond 89 m, one in ten beyond 110 m, and a
quarter more than 34° off the centre of view. Changed: the field of view
narrowed (CHANGING-BEHAVIOUR.md 2.1), and noticing takes longer with
distance, angle off centre and stillness, and much less for someone firing
(2.2). Sandbox (SVER, same seed): 90th percentile of first sightings 100 m
→ 58 m, beyond 90 m 15% → 1%.

**Blocked by objects?** Yes, by everything the level's collision mesh
holds: walls, buildings, rocks, tree trunks. Not by things with no
collision. For MAG that most likely means bushes, foliage and grass
[guess, not checked in game]. MAG keeps its visible geometry in
`environment_high_*.zrb` (about 70 blocks on Valour), which is not
decoded. Adding it to the sight mesh is a research task for the MAG side;
nothing has been started.

**Campers:** the map tool finds vantage points; campers walk to one, face
its best sight line and wait (CHANGING-BEHAVIOUR.md 2.3 and 2.8). The
three Suppression packages were rebuilt with 12 each. Sandbox: 13 camps
in 3 minutes, camping 12% of bot time, the longest holds over two minutes.

Sandbox, SVER, 31 bots, 180 s, seed 1, against 14.6's "after": kills 138 →
129, accuracy 34% → 38% (fights are closer), standing still 3% → 10%,
stuck recoveries 18 → 10, brain 1.2 → 1.0 ms a tick.

**A one-off slow tick** of 60 to 95 ms, about 14 s into a fresh process,
is .NET re-optimising code (it goes away with tiered compilation off or
with re-optimisation brought forward), not the bots. Once a process.

Recording: block 7 holds each camp as it starts (spot and watch
direction). The viewer draws vantage points as blue triangles with "Map
points", says "camping" and draws the watch direction.

### 14.8 Objective roles, and Sabotage (2026-11-17)

The owner asked for objective play so Sabotage can be tested, and set the
shape: a few bots who only play for kills; the rest split into defenders
and attackers, never exactly half and half (the bigger part 51 to 57%);
defenders go to their side's objectives; an objective lost, a player
either goes to defend the other one or goes for the nearest objective;
attackers the same, the other way round. The owner also asked for
research into how people really play. Code: `World.Objectives.cs`.

**Roles.** Per team, from a plan drawn once per match: seekers 6 to 14%
[guess: the owner's "a small number"; no study measures it], and of the
rest the team's natural side (defending, for the team that starts with
more objectives) gets 51 to 57%. Roles are given one bot at a time
against whole-number targets, so the natural side always has the
majority, and a bot that joins later takes whatever the team is short of.

**Which objective.** What each objective is to a team (its *stance*):
take (the other side's, settled, workable), stop (the other side's claim
running down), guard (our claim running down), hold (ours, and the other
side can work it now), calm (ours, nobody can work it yet), locked.
Defenders want stop, guard, hold, and a little calm; attackers want stop,
guard, take. With nothing of its own kind anywhere (the attacking team's
defenders at the start), a bot does the other kind's. Choosing is
weighted: urgent ones first, spread over teammates, nearer likelier, the
current one four times as likely to be kept. Bots think again 0.4 to 2.5
s after an objective changes [guess], and every 35 to 70 s anyway. When
its objective stops being its business, persona `Rotate` (0.3 to 0.7)
decides: the team's other objective of its kind, or the nearest it has
any business at (for a defender that is usually the one just lost: it
goes to retake it).

**Doing it.** Work it: walk to the spot, hold `interact`, eyes mostly on
the work with a glance round now and then. One worker per objective;
teammates stand guard 3 to 10 m out. Defending: a post 6 to 22 m out
(further for the more aggressive), facing where the team last met the
enemy near there, else toward the enemy's spawns, moved every 6 to 18 s.
Campers camp near their objective; attackers camp at 40% of their usual
chance. A hunt leading more than 60 m from the bot's objective pulls at a
third of its strength. Holding `interact` 12 s with no change means the
game refused (MAG locks the uplinks once C opens): the team leaves that
objective until it next changes, four minutes at most.

**Where a bot stands.** The nearest walkable cell within 12 m whose ground
is within 2.5 m of the objective's own height. If that is outside the
reach the body gave, the objective is "not workable": bots fight round it
and never work it. The brain logs this per objective when the match
starts.

**What the research says** (sources checked 2026-11-17):

- The BotPrize Turing tests in Unreal Tournament. The 2011 judges called
  bot-like: firing too soon after first sight, staying locked on while
  dodging, knowing the level (where to go), aim too good. They called
  human-like: spending time observing, running past an enemy without
  shooting, poor tracking, stopping to shoot. Judges expect opponents of
  similar skill. ([UT^2 slides, Schrum, Karpov, Miikkulainen](https://nn.cs.utexas.edu/downloads/slides/schrum.cec2011ws.pdf);
  [2012 result: 52% judged human, people 40%](https://news.utexas.edu/?p=128581))
- DeepMind's Capture the Flag agents (Quake III) learned home-base
  defence, following teammates and camping the opponent's base. Humans
  took 559 ms from first sight to a tag attempt and hit 48%; the agents
  took 258 ms and hit 80%. ([Jaderberg et al. 2019](https://ar5iv.arxiv.org/html/1807.01281))
- A 2025 study of bots in an attack/defend bomb mode compared them with
  28 people over 48.6 hours. The bots moved faster and more steadily than
  people. On defence the bots covered the map "much more evenly" than
  people, who keep to fewer places. Fights clustered near the bomb site.
  ([arXiv 2501.00078](https://arxiv.org/html/2501.00078v1))
- In Counter-Strike, defenders camp hiding spots near the objective and
  attackers rush it. [unverified: a search summary credited this to a
  256-player LAN study; the paper was not opened.] Movement learned from professional rounds was rated more human than
  hand-written bots ([Durst et al. 2024](https://arxiv.org/abs/2408.13934)).
- MAG's developers on Sabotage: the side that makes a focused charge at
  the objective usually wins; long firefights about 100 m out lose even
  with a good kill ratio (a common mistake); smoke 80 to 100 m out to
  regroup; a few attackers round the back of C can break the defenders'
  spawn ([PlayStation Blog, 2010](https://blog.playstation.com/2010/02/09/mag-developer-tips-sabotage/)).
- No study measures how many players ignore objectives. The figures
  found are forum guesses and one unsourced blog figure (87%). The
  seekers' share is the owner's call.
- People respawn and come back one at a time ("staggering"); Overwatch 2
  added group respawn to soften it ([esports.gg](https://esports.gg/news/overwatch/overwatch-2-now-live-with-group-respawn)).

What came from that: defenders keep a post near the objective rather than
spreading over the map; nobody plays an objective from 100 m; workers keep
their eyes on the work, which leaves them open as people are; and bots
react to an objective change after a moment, not at once. Not done yet,
and the next things the research points at: running past an enemy to
reach an objective under threat (BotPrize's "human" sign); attackers
grouping before a push and smoke; a few attackers going round the back of
C; bots chasing the best-known routes less, since "knowing where to go"
read as a bot.

**Sabotage in MAG.** Its three levels have packages now. `mag_kennicott_final` and
`mag_cosmodrome_final` use the usual radius; `mag_remote_comms_final`
needed `--radius 180`, as its defenders spawn 130 m off the line between
the sides. The packages were checked against the brain's stand-spot rule:

| Level | A | B | C |
|---|---|---|---|
| kennicott | workable | workable | not: indoors, nearest ground at its height 11.8 m off |
| remote_comms | workable | not: on a floor the map lacks | not: the console is 8 m above the ground beside it |
| cosmodrome | not: upper floor, 6 m above the street outside | not: same | not: 5.7 m from the nearest ground |

So bots can take and retake uplinks on two levels, and nowhere can they
arm or defuse C. A person can arm C; bots then fight round it but cannot
defuse. Fixing it means interiors and upper floors in the map: task M11
("rooms above rooms"), research.

**Sandbox**, 63 bots and the stand-in person, 600 s, seed 1, `--objectives`
from `RE-Work/mag/v212/maps/<level>.objectives.json` (written by
`tools/re/sabotageobjectives.py`; the sandbox plays the uplinks' rules:
hack 3 s, hold 20 s, undo 3 s, retake):

| | kennicott | remote_comms |
|---|---|---|
| roles | 5 seekers, 28 defenders, 30 attackers | the same |
| uplinks | A at 23 s, B at 27 s, both settled by 47 s, then locked | A changed hands 16 times; B and C not workable |
| kills, accuracy | 535, 37% | 597, 43% |
| standing still | 27% | 17% |
| stuck recoveries | 143 (128 on objectives) | 92 (78) |
| brain | 3.4 ms a tick (perception 3.2) | 1.7 ms |

On kennicott the attackers start nearer the uplinks than the defenders
(about 140 m against 175 m, the level's own spawn points), and in the
sandbox nobody spawns forward, so the uplinks fall fast. Suppression is
unchanged: the baseline command gives the same numbers to the last digit.

Recording: block 8 holds each bot's role and objective whenever either
changes. The viewer draws each objective in its current owner's colour
(a solid ring and a percentage while a claim runs down), says "playing the
objective", and shows the role and objective in the player panel.

**Changed the same day, after the owner watched a recording** (two
things seen: both sides spawned at once; too many defenders sat at C
before it opened, and too many attackers stayed on A and B when C needed
taking):

- *Spawning.* MAG's Sabotage already spawns in waves: the defenders' first
  at 20 s, the attackers' at 35 s, then every 20 s, both teams
  (SpawnInitialDelayTeam, SpawnIntervalTeam, all three levels). The MAG
  server does this for bots and people alike. The sandbox did not: it now
  takes `waves` from the objectives file (`sabotageobjectives.py` writes
  them from the game type table).
- *Too many at a quiet objective.* Teammates were spread evenly over every
  objective a bot had business at, so C before it opened (weight 0.25)
  drew as many defenders as A or B. Each objective's share of the team is
  now in proportion to its weight. And the end of a chain (an objective
  that requires others) counts double once it can be taken or must be held.
- *Attackers left on A and B.* MAG locks A and B for good the moment the
  attackers hold both, and opens C (`gametype_64.lua`, ObjectiveControl).
  The brain had no way to know. The owner agreed a `locked` flag (above).
  A locked objective is nobody's business, so after the lock both sides'
  role players all go to C. The owner's idea of changing the shares as the
  match goes on falls out of this: a role with nothing of its own to do
  does the other's.

Copper Hills again (600 s, seed 7, waves on): attackers hack both uplinks
at 63 s; the defenders undo B at 77 s and retake A at 112 s; the attackers
undo that; both settle and lock at 130 s. At 60 s, 5 of the defending
team's 30 role players are at C; at 200 s all 27 and 30 are. Darien
Network (900 s): A taken 24 times, 15 undone. Suppression baseline
unchanged.
