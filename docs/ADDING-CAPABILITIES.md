# Adding things bots can do

How to give bots a new action, a new kind of objective, vehicles, and
similar. The other guides: [changing how bots behave](CHANGING-BEHAVIOUR.md)
and [adding a game](ADDING-A-GAME.md). The vocabulary this builds on is in
[ATTACH-PROTOCOL.md](ATTACH-PROTOCOL.md), section 6.

## 1. Every capability crosses the same four places

A bot can only do what all four of these agree on. Work through them in
this order.

1. **The vocabulary.** A name in the protocol, the same for every game:
   an action (`crouch`), an objective primitive (`interact`), a control
   mode (`drive`), an event. Section 6 of the spec already names most of
   what shooters and racers need. Add a name only when nothing there fits.
2. **The body.** The game's side says it has the thing (in the manifest),
   reports what the brain needs to know about it (entities, objectives,
   events), and makes it happen when the brain asks.
3. **The brain.** Perception learns about it, `Decide` weighs it, a skill
   carries it out, the motor step turns it into held actions or commands.
4. **The recording and viewer.** What the bot believed and chose gets
   written, or nobody can tune it afterwards.

The brain never learns a game's name for anything. If MAG calls it a
"bunker" and another game calls it a "terminal", both are an `interact`
objective with a duration.

**What is built and what is only named.** Today the brain drives one
control mode (`foot_shooter`), three actions (`fire`, `sprint`, `reload`)
and one objective primitive (`hold_zone`). Everything else in section 6 of
the spec is a reserved name with a field on the wire and no code behind it.

| Thing | On the wire | In the brain | In the MAG body |
|---|---|---|---|
| `fire`, `sprint`, `reload` | Yes | Yes | Yes |
| `interact` (held action) | Yes | Yes (`World.Objectives.cs`) | Yes, at Sabotage's objectives (`BotGame.Interact`) |
| `jump`, `crouch`, `prone`, `aim`, `melee`, `swap_weapon` | Yes (any action name) | No | No |
| `throw`, abilities (aimed at a point or a player) | Yes (`CommandRecord`) | No: never filled | No |
| `hold_zone` | Yes | Yes | Not sent |
| `score_race` | Yes | Accepted, ignored | Sent (Suppression) |
| `interact` objective | Yes | Yes: roles, choice, working it (ATTACH-PROTOCOL.md 14.8) | Sent for Sabotage only (`BotObjectives.cs`) |
| `carry`, `eliminate`, `protect`, `checkpoints`, `survive` | Yes (any primitive name) | No | No |
| `drive`, `turret` control modes | Yes (`AgentRecord.ControlMode`, `EntityRecord.Parent`, `.Seat`) | No: refuses a manifest without `foot_shooter`, drives nothing else | No |
| Lobby choices (team, loadout, spawn point) | Named (`CHOICE`, `CHOICE_REPLY`) | Not acted on | No: the server picks |
| Upper floors, ladders, climbing | No: the map has one walkable layer | No | No |

## 2. A new held action (crouch, jump, aim, melee)

The smallest kind of addition. Crouch as the example.

**Body.**
- Add the name to the control mode's `Actions` list in the manifest. Its
  position in the list is its bit in `ActRecord.Held`. Append, never
  reorder: the brain reads the list, but your own body's constants depend
  on the order (MAG: `BitFire`, `BitSprint`, `BitReload` in `BotGame.cs`).
- Apply it each tick where the body moves the bot (MAG: `Step`, `Walk`),
  and tell the clients (MAG: the character's state field in `Replicate`).
- Report it back: set the matching `EntityFlags` bit (`Crouched`) on the
  bot's entity, and on people's entities too, so bots can see people
  crouch.
- If it changes speed or height, put the number in the manifest
  (`MoveSpec.CrouchMS` exists) and use the lower height in the body's own
  hit test.

**Brain** (`BotBrain.Core`).
- `World.cs`: next to `_bitFire`, add `_bitCrouch = BitOf(mode, "crouch")`.
  A game that does not list the action gives 0, and the bot simply never
  does it. That is how one brain serves games with different actions.
- `Agent.cs`: add `Crouch` to `Intent`.
- `World.Skills.cs`: set `a.Intent.Crouch` in the skill where it belongs
  (in `Hold` while standing guard, in `Engage` behind low cover), by a
  persona habit, not always. Then OR `_bitCrouch` into `held` in
  `RunMotor`.
- If it should affect what bots see (a crouched enemy behind a wall),
  `Perceive` in `World.Perception.cs` uses chest and head heights: lower
  them when the entity's `Crouched` flag is set.
- `Persona.cs`: add the habit if people differ in it.

**Viewer.** Held actions show as chips automatically, from the manifest's
list. Nothing to do.

**Test.** Add the action to the sandbox (`Simulation.BuildManifest`,
`ApplyAct`, movement) so the sandbox exercises it, and a case in
`BotBrain.Tests/BrainTests.cs`.

## 3. An action aimed at something (grenades, abilities)

These are not held buttons. The spec sends them as a command beside the
held set: `CommandRecord` (slot, command id, target entity, a point). The
record exists and `ActFrame` carries a list of them, but the brain never
adds one and no body reads them.

- **Body:** declare the item in the manifest's `Items` (class `thrown`,
  blast radius, projectile speed). Read `act.Commands` where it reads
  `act.Records`, and perform the throw: for a server-side body that means
  simulating the projectile and its blast itself. Report the projectile as
  an entity (`EntityKind.Projectile`) and the blast as an `Explosion`
  event, so bots can run from them.
- **Brain:** a place in `Engage` that decides to throw (enemy remembered
  behind cover, in range, not too close), fills a `CommandRecord` on the
  `ActFrame` in `RunMotor`, and a cooldown. Reacting to an incoming grenade
  is a new case in `OnEvents`.

## 4. A new kind of objective

`interact` was the first one built after `hold_zone` (2026-11-17), and it
is the pattern to follow. What it means on the wire is in
ATTACH-PROTOCOL.md section 6.3; how the brain plays it is 14.8.

**Body.**
- List the primitive in the manifest's `Objectives`, and any action it
  needs in the control mode's `Actions` (here `interact`).
- Send `OBJECTIVES` when the match goes live: one entry per live
  objective with `primitive`, position, radius, `owner_team`, `progress`,
  `requires` and `locked`. The brain is told the order ("take A and B, then C" is C
  requiring A and B); it never learns the mode's name.
- Send an `Objective` event whenever one changes hands or progresses.
  MAG's body polls each objective's state every tick and sends one when the
  owner changes, the progress moves by a twentieth, or it settles
  (`BotObjectives.Poll`).
- Make the action work for a bot: when a bot holds the action within
  reach, run the same path the game runs for a person. MAG:
  `MagGameComponent.BotWork` starts a step under the same checks as a
  person's `NetMessageObjectiveMessage` (`WhyNotActivate`, `WhyNotDeactivate`),
  and `BotLetGo` releases it as a person's release does. The body calls
  them outside its own lock, because a step's change can set off a blast
  whose damage comes back into the body.

**Brain** (where `interact` went, for the next primitive):
- `BrainServer.Open`: the name in the accepted list.
- `World.Decision.cs` `SetObjectives` hands the message to each
  primitive's reader (`SetSites` for `interact`); `OnObjective` passes
  events on (`OnSiteEvent`).
- `Agent.cs`: the goal (`Goal.Objective`) and the agent's state for it
  (role, objective, post, working).
- `Decide`: score it against chasing, hunting and camping (`SitePull`),
  and pick it.
- A skill (`PlaySite`), added to `RunSkill`; the held action added to
  `RunMotor`.
- `World.Recording.cs`: `DestinationOf`, and a block for what is new to
  decide (block 8, role and objective).
- Stats: a line in `BrainStats.Report`.

**Viewer** (`viewer/index.html`): the goal's name in `GOAL` and
`GOAL_TEXT`, and whatever the new block shows (the role in the player
panel; objectives drawn in their owner's colour from the objective events).

**Sandbox**: `InteractObjectives.cs` plays a file of objectives
(`--objectives`) by simple rules, so the brain can be tried without the
game. Any new primitive wants the same.

**MAG, the other modes.** Only Sabotage is described to the brain.
Acquisition (vehicles to steal), Domination (a chain of objectives with
bunkers, AA and repairs) and Escalation (three teams, neutral objectives
that belong to whoever takes them) each need their own look at the
server's objective table before `BotObjectives` sends them. And most of
MAG's objectives are indoors or on upper floors that the one-layer map
does not have (14.8); that is task M11.

## 5. Vehicles and mounted guns

The largest addition, because it is a second way of controlling a
character, not one more button. A vehicle seat changes the agent's control
mode (`drive`, `turret`); each mode has its own axes, its own skills and
its own model of the hands.

**Protocol: already has room.** `AgentRecord.ControlMode` says which mode
an agent is in this tick. A vehicle is an entity of kind `Vehicle`. A
rider's entity names it in `Parent` with a `Seat` number. `SeatChange` and
`ControlMode` events exist. `enter_exit` is a named action.

**Brain: not started.**
- `World` reads only the `foot_shooter` entry of the manifest and throws
  without one. It needs the look and move settings per mode, and to pick
  skills and motor by `AgentRecord.ControlMode` each tick.
- `drive` needs a driving skill (follow a route, brake for corners, avoid
  others) and a steering model, which is not the thumb-on-a-look-stick
  model in `RunMotor`.
- `turret` is the nearest to what exists: `Engage` with no movement.
- Deciding to get in, as driver or gunner or passenger, and when to get
  out, goes in `Decide`.
- The map has no roads. A vehicle needs its own walkable layer with its
  own width and turning limits; the spec reserves a `drive` link type and
  the map builder does not produce it.

**Body.** A real-client body gets vehicle physics for free from the game.
A server-side body (MAG) has to move the vehicle itself, which means
writing vehicle movement that looks right to every client. That is a
large, separate piece of work and should be judged on its own.

A reasonable order: `turret` first (mounted guns and vehicle gunner seats,
small), passengers next, `drive` last.

## 6. Other things that are map work, not behaviour

- **Upper floors, stairs with rooms above, bridges:** the map package
  stores one ground height per cell. Bots cannot go under something they
  can also walk on top of. This needs a layered map or a navigation mesh
  in `BotBrain.Maps`.
- **Ladders, vaults, climbing:** link types reserved in the spec; the map
  builder makes `walk` and one-way `drop` links only.
- **Cover:** the builder makes no cover points. `Hold` picks random spots
  in a zone and `Engage` sidesteps in the open.
- **The level's real boundary:** for MAG the builder cuts at a radius
  around the spawns, because the boundary is not decoded.

## 7. Checklist for any addition

- [ ] The name is in the spec's vocabulary (section 6), added if new.
- [ ] The body lists it in the manifest, reports it, and applies it.
- [ ] A game that does not list it still works: the brain's bit or
      objective list comes out empty and the bot never tries.
- [ ] `WELCOME` accepts it (`BrainServer.Open`).
- [ ] The decision is scored in `Decide`, with the current goal favoured.
- [ ] The behaviour varies by persona where people vary.
- [ ] It is written to the recording and drawn in the viewer.
- [ ] The sandbox does it, and a test covers it.
- [ ] The table in section 1 of this file and section 14.3 of the spec are
      updated.
