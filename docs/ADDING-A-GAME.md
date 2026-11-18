# Adding a game

How to give another game bots. The brain does not change. What you write
is that game's **body**: the piece that tells the brain what is happening
in a match and carries out what the brain asks. The other guides:
[changing how bots behave](CHANGING-BEHAVIOUR.md) and
[adding things bots can do](ADDING-CAPABILITIES.md). The wire format is in
[ATTACH-PROTOCOL.md](ATTACH-PROTOCOL.md).

MAG is the one game done so far and is the worked example throughout. Its
body is three files in `Plugin.MagGameServer/Battle/`: `BotGame.cs`,
`MagGameComponent.Bots.cs` and `BotMath.cs`.

## 1. Can the brain play this game today?

Check before anything else. The brain as built plays:

- on foot, first- or third-person, with guns (`foot_shooter`);
- team against team, with either nothing to capture or areas to stand in;
- on a map with one walkable level (no rooms above rooms).

A racing game, a stealth game with lock-on and climbing, or a mode built on
vehicles needs brain work first; see
[ADDING-CAPABILITIES.md](ADDING-CAPABILITIES.md). The spec's section 11
writes five titles out in protocol terms and says what each would need.

## 2. Choose the kind of body

This follows from one question: **in this game, who decides where a
player's character is and whether its shots hit?**

| Who decides | Kind of body | What you write | Example |
|---|---|---|---|
| Each player's own client (the server relays and trusts it) | **Server-side.** A bot is a seat with no connection. | The server moves each bot, checks its shots against the level, and sends the clients what a real client would have sent. | MAG. Built. |
| A dedicated server that simulates everyone from their inputs | **Server-side, lighter.** | Feed the bot's stick and buttons in where a client's input arrives. The game does the rest. | None yet. |
| One player's machine hosts, or it is peer to peer, on PC | **Real client.** One running copy of the game per bot, driven through a virtual pad by the shim. | Read the game's state from memory, write pad input. | None yet. Fits small-lobby PC titles. |
| As above, but a game copy per bot is too heavy | **Headless client.** A program that speaks the game's network protocol as a player. | The game's whole client protocol, plus movement and shots. | None yet. The most work. |

Count the cost of the first row honestly: a server-side body for a game
whose clients own their characters has to do the game's movement and
shooting itself. For MAG that is about 700 lines, and it still cannot
jump, crouch or climb.

## 3. What you need from the game

Find all of these before writing code. Each one missing is a research task.

- [ ] **Where everyone is, every tick:** position, facing (yaw and pitch),
      alive or dead, team, for people and bots alike.
- [ ] **A way to make a bot appear** as a player to everyone in the match:
      name, team, character, spawn.
- [ ] **A way to move it:** either inject pad input, or set its position
      and facing and have the game replicate that.
- [ ] **Damage both ways:** how a bot's hit is applied to a person, and
      how a person's hit on a bot reaches you.
- [ ] **Events:** who was hit by whom, who died, who fired.
- [ ] **The level's solid geometry** as triangles, and the spawn points.
- [ ] **The game's axes and units,** and what its facing angle means
      (which way is zero, which way it grows).
- [ ] **How fast a character runs, sprints and turns,** measured, and the
      look stick's dead zone and response curve if you can find them.
- [ ] **Objectives:** where they are and when they change hands, if the
      mode has any.

## 4. Build the map package

The brain needs the level to find routes and to know who can see whom. It
takes two files from you and makes the package itself.

**The level's triangles**, as a "soup" file. All little-endian:

```
'BBSP'              4 bytes
u32 1               version
u32 vertex count
u32 triangle count
vertices            3 × f32 each: x, y, z
triangles           3 × i32 each: indices into the vertices
```

in the **brain's axes: metres, z up, x and y on the ground.** Convert from
the game's axes here, once (MAG is y-up, so brain x = MAG X, brain y =
MAG Z, brain z = MAG Y). Use the collision geometry, not the drawn
geometry, if the game has both. Place every object where it stands in the
level; a common mistake is to export models un-placed at the origin.

**The spawn points**, as JSON, in the same axes:

```json
{ "level": "mag_sver_sup_final",
  "points": [
    { "kind": "spawn",  "team": 0, "id": 0, "x": 182.9, "y": 391.2, "z": 124.0 },
    { "kind": "supply",                      "x": 201.5, "y": 240.0 } ] }
```

`spawn` points are where the walkable area is grown from, so there must be
at least one per team and they must be on or just above the ground. Any
other `kind` becomes a place bots head for when there is nothing to
capture; the tool adds more of its own, spread over the ways between the
two sides' spawns (not the far edges of the map, where nobody goes).

**Build it:**

```sh
dotnet run --project BotBrain.MapTool -c Release -- build level.soup level.points.json maps/
```

This writes `maps/<level>-<hash>.bbmap` and `maps/<level>.png`. **Open the
picture.** It shows the walkable ground from above, lighter where higher,
with the spawns marked. Check that it looks like the level, that both
spawns are inside the walkable area, and that the two sides connect. The
tool also prints how many spawn points stand on walkable ground; all of
them should. The yellow dots are the interest points: they should sit in
the lanes and the middle, not on the edge of the cut-out area. The cyan
dots are vantage points for campers: they should overlook open ground, on
ledges, wall ends and building edges. The tool
says whether it found routes between the two sides; if it did not, the
sides do not connect, or the spawn teams are wrong.

What usually goes wrong, from MAG:

- *Only the spawn buildings are walkable:* the terrain was missing from
  the export, or spawns stand on platforms above the ground (the builder
  allows a one-way drop of up to 4 m).
- *The whole terrain is walkable, far beyond the playable area:* the level
  has no walls at its edge. `--radius` cuts at a distance around the line
  between the two teams' spawns (130 m by default). The real boundary, if
  you can read it, is better.
- *Bots wedge on corners:* the map and the body disagree about how wide a
  character is. The builder probes 0.25 m to each side.

`--cell` sets the grid size (0.5 m by default). Limits of the package are
in the spec, section 9.1: one walkable layer, no ladders, no cover points.
Sight uses the same triangles as walking, so anything that hides a person
but has no collision (bushes, grass, cloth, smoke) does not hide them from
bots. If the game keeps those in separate render data, adding them to the
sight mesh only (not the walking grid) is what would fix it; nothing does
that yet.

MAG's exporter is `RE-Work/farcry-lan/MAG/tools/re/collision.py soup`.
Each engine needs its own.

## 5. Write the body

In C# you can reference `BotBrain.Protocol` (framing, messages, records)
and `BotBrain.Maps` (the map package, movement on it, rays). In any other
language, implement the framing and records from the spec, sections 3 and
7; they are small.

### 5.1 The conversation

```
connect to the brain (TCP)
→ HELLO       the manifest: what this game is (5.2)
← WELCOME     map_loaded must be true, or the brain drives nothing
→ SEATS       one entry per bot you want: slot number, team
← SEAT_TAKE   one per seat: the bot's name. Create the player in the game now.
→ OBJECTIVES  the live objectives (an empty list if there are none)
→ PHASE       "live"
every tick:
→ EVENT       what happened since the last tick, if anything (5.5)
→ OBSERVE     where everyone is (5.3)
← ACT         controls for the bots (5.4), read on its own thread
at the end:
→ BYE, close
```

A frame is `u32 length` (of the type byte plus the payload), `u8 type`,
payload. The one-off messages are JSON with snake_case names. `OBSERVE`,
`ACT` and `EVENT` are packed binary records.

**Never wait for `ACT`.** Send `OBSERVE` on your own clock and apply
whatever controls have arrived. If the brain is slow or gone, the game
carries on, and bots whose controls are more than a quarter of a second
old should go to neutral. The sandbox's `BodyLink.cs` is the smallest
complete example (about 150 lines); MAG's `BotGame.Start`, `TickLoop` and
`ReadLoop` are the real one.

### 5.2 The manifest

This is how the brain learns the game without knowing it. Get these right:

| Field | What to put |
|---|---|
| `tick_hz` | How often you send `OBSERVE`. 30 is what the brain was tuned at. |
| `control_modes.foot_shooter.actions` | The actions you will carry out, in bit order: `["fire", "sprint", "reload"]`. Leave out what you cannot do; the brain will not try it. |
| `control_modes.foot_shooter.look` | `max_turn_deg_s`, `max_pitch_deg_s`, `deadzone`, `curve_exponent`: how the game turns look-stick deflection into turn rate. **Measure these.** The brain works out its stick movements by inverting this curve, so wrong values make every bot turn wrongly in the same way. |
| `control_modes.foot_shooter.move` | Run and sprint speed in m/s. |
| `teams` | The team ids, and each pair that are enemies. |
| `objectives` | The primitives this mode uses. |
| `map` | The package's id and hash (`GridMap.Id`, `GridMap.Hash()`), so both sides are certain they have the same level. |

`WELCOME` lists what the brain accepted and what it does not support. Log
the rejected list; it is the fastest way to see what bots will ignore.

### 5.3 OBSERVE: tell the brain the truth

Every tick, one record for **every** player in the match, people included,
in the brain's axes:

- **id:** stable for the match and **never 0** (0 means "unknown" in
  events). MAG uses the character index plus one.
- **flags:** `Alive`, and `Human` on people. Bots treat everyone alike;
  `Human` only decides which bots get full effort.
- **position** of the feet, **velocity** (estimate it from position
  changes if the game does not give it), **yaw** and **pitch** in radians.
  Yaw 0 looks along +x and grows counter-clockwise seen from above; pitch
  is positive looking up.
- **health** from 0 to 254.

Then one agent record per bot (its slot, the entity it controls, rounds in
the magazine and in reserve), and one observer record per person (where
their camera is), which the brain uses only to decide which bots a person
could be looking at.

Send the truth, including enemies behind walls. Hiding what a bot should
not know is the brain's job, done the same way for every game.

**Getting the angles right is the commonest source of bugs.** Write the
conversions once, with a test, in one file (MAG: `BotMath.cs`), and
calibrate against the real game: MAG's yaw convention was read off 17,000
logged steps of sprinting players, by comparing which way they were moving
with the yaw the client sent.

### 5.4 ACT: carry out what the brain asks

Each record is for one bot: four stick axes from −32767 to 32767 and a set
of held actions.

| Axis | Meaning |
|---|---|
| 0 | Move stick, right is positive |
| 1 | Move stick, forward is positive (relative to where the bot faces) |
| 2 | Look stick, right is positive (turning right, so yaw goes **down**) |
| 3 | Look stick, up is positive |

The brain sends a thumb's stick positions, not angles and not a
destination. That is deliberate: the human look of the aiming comes from
the stick going through the game's own dead zone and response curve.

- **A real-client body** writes these to the virtual pad and is done.
- **A server-side body** must turn them into movement itself. Each tick,
  per bot:
  1. **Turn:** apply the same dead zone and curve you declared in the
     manifest to axes 2 and 3 (`BotMath.StickCurve`), times the top turn
     rate, times the tick length.
  2. **Move:** rotate axes 0 and 1 by the bot's yaw, scale by run or
     sprint speed, and step with `GridMap.Move(from, to)`, which stops at
     walls and edges. Take the height from `GridMap.GroundAt`, and let the
     bot fall when it steps off a drop.
  3. **Shoot:** while `fire` is held and the gun is ready, cast a ray from
     the bot's eye along its yaw and pitch against the level
     (`GridMap.Mesh.Raycast`) and against each enemy's body, and hand a hit
     to the game's own damage code. Count rounds and reload times yourself.
  4. **Tell the clients:** send what a real client would send about its
     character, at the rate a real client sends it.

  MAG's `Turn`, `Walk`, `Weapon`, `Fire` and `Replicate` in `BotGame.cs`
  are exactly these four steps. The sandbox's `Simulation.cs` has the same
  logic with no game attached.

**The shared "body kit" is not built.** The spec plans one library for
steps 1 to 3 so each server-side body only translates to its game. Today
that code lives in MAG's `BotGame.cs` and the sandbox. The second
server-side body should pull it out into a shared project instead of
copying it.

### 5.5 Events

Send these as they happen; the brain's sense of danger depends on them.

| Event | Fields | Why the brain needs it |
|---|---|---|
| `Damage` | A = victim, B = attacker, X Y Z = direction from victim toward the source, Value = amount | A bot that is shot turns to look. |
| `Death` | A = victim, B = killer | |
| `Shot` | A = shooter, X Y Z = where from | Bots nearby hear it and may come. |
| `Objective` | A = objective id, B = new owning team | Squads are re-spread. |

Send `Shot` for people as well as bots if the game tells you when a person
fires. MAG's server only learns of a person's shot when it hits, so bots
there are deaf to a person's misses.

### 5.6 The rest of a player's life

Things a person's client does that a bot's body must do for it:

- **Joining:** create the player when `SEAT_TAKE` arrives, with the name
  the brain gave. Decide how many bots to ask for (MAG: fill each team up
  to `FillPerTeam`).
- **Choices the game expects:** team, class, loadout, spawn point, "ready".
  The protocol has messages for letting the brain choose; they are not
  built, so the body picks (MAG: default kit, spawn points shared out).
- **Respawning:** ask to respawn after a pause of a few seconds, as a
  person reading the death screen would, not at once.
- **Leaving:** remove the bots when the last person leaves or the match
  ends, the way the game removes a player who quits.

### 5.7 Where the brain runs

- **In the game server's process** (C# only): create a `BrainServer` on a
  loopback port and connect to it, as `MagGameComponent.Bots.cs` does.
  Nothing else to start. The bodies still talk to it over the protocol.
- **As its own process:** `dotnet run --project BotBrain.Host -- --port
  24800 --maps maps --record recordings`. Any language can attach, and one
  brain can serve several games at once.

Either way the brain and the body must be given the same map package.

## 6. Bring it up in this order

Each step catches a different class of mistake, cheapest first.

1. **The map alone.** Look at the PNG.
2. **The brain on your map, with no game:**

   ```sh
   dotnet run --project BotBrain.Sandbox -c Release -- --agents 31 --seconds 180 \
       --map-file maps/<level>-<hash>.bbmap --record recordings
   ```

   Open the recording in `viewer/index.html`. Bots should cross the map,
   meet and fight. Watch the printed "stuck recoveries" and "standing
   still": high numbers mean a map problem.
3. **An automated test of your body** with a test client, if the game's
   server has one: bots are seated, appear, spawn, move, and damage works
   both ways. MAG's is `Plugin.MagGameServer.Tests/BotTests.cs`.
4. **Live, with few bots** (MAG started with four a side), recording on.
   A game client can refuse or crash on a player nobody is behind; find
   that out with four before trying sixty.
5. **Check by eye what no test can:** are bots standing on the ground, not
   in it or above it; do they face the way they walk; do they aim up at
   someone above them, not down; can you see their gun fire; do your hits
   register on them. Each of these is a single convention in your
   conversion code.
6. **Then tune**, with recordings: [CHANGING-BEHAVIOUR.md](CHANGING-BEHAVIOUR.md).

## 7. What a finished body consists of

For orientation, MAG's:

| Piece | Where | Size |
|---|---|---|
| Level export to triangles and spawn points | `RE-Work/farcry-lan/MAG/tools/re/collision.py` | 225 lines of Python, after working out the file format |
| Axis and angle conversions | `Battle/BotMath.cs` | 60 lines |
| The link, the tick, movement, shooting, replication | `Battle/BotGame.cs` | 700 lines |
| Seating, showing, removing and damaging bots in the game's own terms | `Battle/MagGameComponent.Bots.cs`, small edits in `MagGameComponent.cs` | 250 lines |
| Settings | `MagBotSettings` in Plugin.Medius | 30 lines |
| Test | `Plugin.MagGameServer.Tests/BotTests.cs` | 250 lines |

The export and the two "in the game's own terms" pieces are the
game-specific research. The rest is the same for every server-side body
and is what the body kit should absorb.
