# Start here

You are picking up **BotBrain**: one program (the brain) that plays as many
human-like bot players, in any game that attaches to it. The owner wants
old online games to feel populated again. Bots must feel like real players
in the match. No chat, no voice.

Read this file to the end before doing anything. Then read the guide for
the task you were given. Do not start from the code.

| If your task is | Read |
|---|---|
| Any task | This file, then section 14 of [docs/ATTACH-PROTOCOL.md](docs/ATTACH-PROTOCOL.md) (what is built) |
| Bots behave wrongly or look fake | [docs/CHANGING-BEHAVIOUR.md](docs/CHANGING-BEHAVIOUR.md) |
| Bots should do something new (crouch, capture, drive) | [docs/ADDING-CAPABILITIES.md](docs/ADDING-CAPABILITIES.md) |
| A new game, or a new level of a game | [docs/ADDING-A-GAME.md](docs/ADDING-A-GAME.md) |
| Anything about MAG's server or its file formats | `../RE-Work/farcry-lan/MAG/docs/` (`JOURNAL.md` is the history, newest last) |

## 1. Rules. These are the owner's, and they are not negotiable

1. **Never start, stop or restart `ServerApp`.** The owner runs it. You
   build and deploy; they restart and play. Check whether it is running
   before you deploy (section 4).
2. **Never screenshot or capture a game window.** You cannot see the game.
   Ask the owner what is on screen, in plain questions they can answer by
   looking.
3. **The brain knows no game.** Nothing in `BotBrain.Core`, `BotBrain.Maps`
   or `BotBrain.Protocol` may name a game, read a game's files, or contain
   a number that is only true for one game. Game facts go in that game's
   body and reach the brain through the manifest.
4. **Do not commit or push unless the owner asks.** Say what is
   uncommitted when you finish.
5. **Never kill a process by name pattern** (`pkill -f`, `pgrep -f`). It
   has killed the shell before. Find the pid and kill that.
6. **Do not claim something works because tests pass.** Tests prove the
   server's side. Only the owner playing proves the game client's side.
   Say which of the two you have.
7. **Do not guess at a file format, a wire field or a game convention and
   build on the guess.** Mark it `[guess]` where you write it down, and
   find a way to check it, or ask.
8. **Stop and ask the owner** before: changing the protocol's wire format,
   changing the design in section 2, deleting or overwriting data you did
   not create, any task labelled *research* below once you have findings
   (report first, build second), and whenever a task takes you outside the
   folders in section 3.

## 2. The design, in six sentences

The **brain** is one service with no game knowledge. Each game has a
**body** that tells the brain where everyone is every tick (`OBSERVE`) and
carries out the stick and button positions the brain sends back (`ACT`).
The brain hides from each bot what it should not know (walls, reaction
time), decides what it wants, and moves a model of a thumb on a stick, so
the aiming looks human because it goes through the game's own stick
response. A game describes itself in a **manifest** from a fixed
vocabulary: control modes (on foot, driving), actions (fire, sprint),
objective kinds (hold an area, pass checkpoints). The brain needs a **map
package** per level, built offline from the level's triangles. Every match
can be **recorded** and replayed from above in `viewer/index.html`.

The owner decided: one brain and many bodies; it must run on an ordinary
PC with MAG's 256 players as the ceiling; the brain's code may be GPL.

## 3. Where everything is

All paths are from `/home/euler/Documents/projects`.

| Path | What | Version control |
|---|---|---|
| `BotBrain/` | The brain, the sandbox, the map tool, the viewer, these docs | **Not a git repository.** There is no undo. Ask the owner before large rewrites. |
| `Plugin.MagGameServer/` | MAG's game server. The MAG body is `Plugin.MagGameServer/Battle/BotGame.cs`, `MagGameComponent.Bots.cs`, `BotMath.cs`; test in `Plugin.MagGameServer.Tests/BotTests.cs` | git, with uncommitted work |
| `Plugin.Medius/` | MAG's lobby server. Bot settings: `MagBotSettings` in `Plugin.Medius/Extensions/Mag/MagExtension.cs` | git, with uncommitted work (not all of it about bots) |
| `medius-testserver/` | The running test server: `Plugins/`, `Dependencies/`, `maps/`, `recordings/`, `Titles/MAG/medius/title.json` | not git; `*.bak-before-bots` are the pre-bot files |
| `RE-Work/farcry-lan/MAG/` | MAG notes (`docs/`) and tools (`tools/re/`). `collision.py` exports a level | not git |
| `RE-Work/mag/v212/` | Extracted MAG 2.12 data: `levels/` (level files), `maps/` (built map packages and pictures) | not git |
| `UplaySDKShim/` | The DLL already injected into the Ubisoft PC games. Far Cry 3's body will live in or beside it | git |
| `gta4-mp-gate/` | The mod that reopens GTA IV multiplayer. A GTA IV body would live in or beside it | git |

## 4. Commands

Build and test. Run both after every change; both must pass before you
report anything.

```sh
cd /home/euler/Documents/projects/BotBrain && dotnet test BotBrain.slnx -c Release
```
```sh
cd /home/euler/Documents/projects/Plugin.MagGameServer && dotnet build -c Release && dotnet test -c Release --no-build
```

The second takes about a minute; one test waits 20 s for a spawn wave.
Expect 35 brain tests, 265 MAG server tests and 258 lobby tests.

Try the brain with no game, on a real MAG level, with a recording:

```sh
cd /home/euler/Documents/projects/BotBrain && dotnet run --project BotBrain.Sandbox -c Release -- --agents 31 --humans 1 --seconds 180 --seed 1 --map-file ../RE-Work/mag/v212/maps/mag_sver_sup_final-6acdb3849f1d627c4459e7ae886524a2a5c1715ff532f56b09720f0db16b8a22.bbmap --record /tmp/bbrec
```

Baseline for that exact command (2026-11-17, after the shooting change):
128 kills, 43.1% hit rate, standing still 10%, 7 stuck recoveries, brain
about 1.0 ms a tick. The hunting line reads 63 revenge, 77 toward
gunfire, 26 round the side, 8 camps. (Before it, 2026-11-16: 129 kills,
38.4%; the hit rate rose because far shots, which mostly miss, are now
rare.) Hit rate by distance is the `Bots_hit_like_people_close_up_and_far_away`
test, a shooting range in `BrainTests.cs`. Older packages are in
`maps/superseded/` folders. If your change
moves these a lot and you did not mean it to, you broke something.

Look at a recording: open `BotBrain/viewer/index.html` in a browser and
drop the `.bbrec` on it. To check it yourself, serve the folder over http
and open `index.html?file=name.bbrec`.

Deploy to the test server. First see whether the server is running:

```sh
ps -eo pid,comm | grep -i serverapp
```

Build what you deploy from the **project file**, so the brain is built in
Release (the folder's `.slnx` does not list the brain's projects, and
MSBuild then builds them in Debug; every deploy until 2026-11-17 11:24
shipped an unoptimised brain):

```sh
cd /home/euler/Documents/projects/Plugin.MagGameServer && dotnet build Plugin.MagGameServer/Plugin.MagGameServer.csproj -c Release
```

Copy these, each to a temporary name in the destination folder and then
`mv` over the old file (never `cp` straight over a file a running server
has loaded):

- from `Plugin.MagGameServer/Plugin.MagGameServer/bin/Release/net10.0/`:
  `Plugin.MagGameServer.dll` and `.pdb` → `medius-testserver/Plugins/`;
  `BotBrain.Protocol.dll`, `BotBrain.Maps.dll`, `BotBrain.Core.dll` →
  `medius-testserver/Dependencies/`
- **since 2026-11-17 12:20, also** `DotRecast.Core.dll` and
  `DotRecast.Detour.dll` from `~/.nuget/packages/dotrecast.core/2026.3.1/lib/net10.0/`
  and `~/.nuget/packages/dotrecast.detour/2026.3.1/lib/net10.0/` →
  `medius-testserver/Dependencies/`. BotBrain.Maps needs them (the
  navigation mesh, M11) and the MAG build does not copy them; without them
  the server fails as soon as it loads a map.
- only if `MagBotSettings` changed, from
  `Plugin.Medius/Plugin.Medius/bin/Release/net10.0/`: `Plugin.Medius.dll`
  and `.pdb` → `medius-testserver/Plugins/`

Then tell the owner it takes effect at the next restart. Another session
also works in `Plugin.MagGameServer/` (game rules, replication): a build
there includes its uncommitted work, so tell the owner what goes out. Bot
settings are the `"Bots"` line in `medius-testserver/Titles/MAG/medius/title.json`
(that file has comments in it; a strict JSON parser will reject it, which
is not a fault).

Build a map package for another MAG level (the disc is at
`/mnt/steamdrive/SteamLibrary/ps3/MAG (USA)/PS3_GAME/USRDIR/run/mag/ps3/levels/`,
the 2.12 patch at
`~/.config/rpcs3/dev_hdd0/game/BCUS98110/USRDIR/patches/patch_r000000_445230.psarc`;
`L` is the level name, such as `mag_cosmodrome_final`):

```sh
cd /home/euler/Documents/projects/RE-Work
RX="$L/($L\.clv|resource_blocks/physics\.zrb)\$"
python3 farcry-lan/MAG/tools/re/psarc.py "<disc levels folder>/$L.psarc" mag/v212/levels "$RX"
python3 farcry-lan/MAG/tools/re/psarc.py "<patch archive>" mag/v212/levels "$RX"
python3 farcry-lan/MAG/tools/re/collision.py soup mag/v212/levels/run/mag/ps3/levels/$L mag/v212/maps/$L.soup \
    ../Plugin.MagGameServer/Plugin.MagGameServer/Data/mag-spawn-points.json \
    ../Plugin.MagGameServer/Plugin.MagGameServer/Data/mag-level-objects.json
cd ../BotBrain && dotnet run --project BotBrain.MapTool -c Release -- build ../RE-Work/mag/v212/maps/$L.soup ../RE-Work/mag/v212/maps/$L.soup.points.json ../RE-Work/mag/v212/maps --no-navmesh
```

`--no-navmesh` keeps the package at version 2 (grid only). Without it the
tool also builds the navigation mesh and finds the roam and camp points on
it (version 4, M11), which the deployed brain and MAG body use since
2026-11-17; build those into
`maps/v3/` and pass `--objectives <file>` for a Sabotage level to see each
objective's floor. The Sabotage levels are deployed as version 3, the
Suppression levels still as version 2 (see M11).

Extract the disc archive first and the patch second, so the patch's files
win. **Open the PNG it writes and check it** against section 4 of
ADDING-A-GAME.md before copying the `.bbmap` to `medius-testserver/maps/`.
The three Suppression levels and the three Sabotage levels
(`mag_kennicott_final`, `mag_remote_comms_final` with `--radius 180`,
`mag_cosmodrome_final`) are built and deployed. Each Sabotage picture looks
like its level. On each, 16 of 20 spawn points stand on walkable ground
(the four that do not are the attackers' parachute drop zones, high in
the air), and the exporter takes about 11 resources as "already in world
space". Expect each new level to show something like this, and expect to
adjust `--radius` on the larger ones (remote_comms' defenders spawn 130 m
off the line between the sides). For a Sabotage level, also write its
sandbox objectives file:
`python3 farcry-lan/MAG/tools/re/sabotageobjectives.py ../Plugin.MagGameServer/Plugin.MagGameServer/Data/mag-objectives.json $L mag/v212/maps/$L.objectives.json`,
and try it: the sandbox command above with `--objectives` and that file.

## 5. What "done" means for any task

1. Both test suites pass.
2. If behaviour changed: the sandbox command above was run before and
   after with the same seed, you compared the numbers, and you looked at
   the recording.
3. If something new can be done or decided: it is recorded and visible in
   the viewer, and a test covers it.
4. The docs say what is now true: the guide you followed, the tables in
   ADDING-CAPABILITIES.md section 1 and ATTACH-PROTOCOL.md section 14.3,
   and for MAG a dated entry at the end of
   `RE-Work/farcry-lan/MAG/docs/JOURNAL.md`.
5. Deployed, if it is for MAG, with the owner told to restart.
6. Your report says: what changed, what you verified and how, **what only
   the owner can verify by playing** (as questions they can answer by
   looking), and what is uncommitted.

## 6. Where things stand (2026-11-17)

Works, played by the owner: MAG 2.12 Suppression with 4 bots a side. Bots
join, spawn, move, fight people and each other, die, respawn. The owner's
words: "Bots worked great". They could not judge whether the movement
looked human.

Built since, not yet used on a real match: the recorder and the viewer.

Not known:
- Whether bots aim up and down the right way round (pitch sign is a guess
  in `BotMath.cs`).
- Whether a person can see a bot's gun and see it firing.
- How it looks on a real PS3 (played so far on RPCS3).
- MAG's real turn rate and look-stick curve (`Look` in `BotGame.cs` is a
  guess).
- How many bots a client tolerates. Only 4 a side has been tried.

Known to be wrong: a bot that spots an enemy snaps to top turn speed and
holds it (CHANGING-BEHAVIOUR.md section 2.5).

Changed 2026-11-16 after the owner watched a recording ("constantly
sweeping the back where there is nothing"), deployed, **not yet played**:
roam points are on the ways between the sides (maps rebuilt), roaming is
weighted toward where the team last met the enemy, and bots hunt: back
for their killer, toward distant gunfire, some round the side, by persona.
Spec section 14.6 has the before and after, and the owner's first match
with it (bots never stop and sprint too much: not fixed yet).

Changed 2026-11-16 after the owner said bots see too far and too wide and
asked for campers, deployed, **not yet played**: human-like noticing
(distance, angle, stillness) and a narrower view; campers at vantage
points the map tool now finds (maps rebuilt again). Bushes and foliage
still do not block bots' sight in MAG (render data not decoded). Spec
section 14.7.

Changed 2026-11-16 at the owner's request ("fill lobby"), deployed, **not
yet played**: bots arrive as soon as a person connects to a game (so they
are there while the person loads), and with `"FillToTeamSize": true` (now
on in title.json) fill both teams to the mode's size: 63 bots with one
person in Suppression. Only 4 a side had been played before, so M7 is
effectively being tested at the top. A person placed on a team takes a
bot's seat; the team is refilled 10 s after someone leaves. To go back,
remove `"FillToTeamSize": true` (then `FillPerTeam` applies).

Played 2026-11-16 22:07 with the full lobby: the owner saw "the bots all
disappearing then respawning all together". Cause: a brain crash, three
times (IndexOutOfRange in `Camp`). A camp ended on the way to its spot,
Decide returned early because the tick's two path searches were used up,
the agent kept `Goal.Camp` with spot -1, and the next camp skill read
`_campPoints[-1]`. The exception ended the link, and the body takes every
bot out when the link ends, then seats 63 new ones. Fixed and deployed
2026-11-17 00:13, **not yet played**: `EndCamp` clears `Goal.Camp`, the
camp skill checks its spot, and `World.Tick` now catches an exception
from one agent, resets that agent, counts it (`Stats.Faults`, a `FAULTS`
line in the summary) and logs it ("agent fault #n", for faults 1, 2, 4, 8 and so on), so one bot's bug no longer
ends the match for all of them. A fault in the log is still a bug to fix.

Shooting changed 2026-11-17 at the owner's request (a bot killed them with
5 hits of 9 at 85 m), deployed, **not yet played**. Live before: bots hit
63-68% of shots under 20 m, 41-46% at 35-50 m, 23-31% at 50-70 m, 15-18%
at 70-90 m. Brain: a held aim error per target that grows with distance
and is walked in while firing (`AimBias`), a trigger that fires when
roughly on (`TriggerFloor`), short bursts beyond 40 m, and no fire beyond
`HoldFireRange` unless camping or shot at. Gun (MAG body and sandbox):
MAG's per-weapon spread from weapons.lua, units [guess]. Shooting range
after: about 43% up close, 14% at 60-120 m when the target is shooting at
the bot (was about 40% there). Close range is unchanged on purpose: the
owner asked about long range.

Objective play for Sabotage, 2026-11-17, at the owner's request,
deployed, **not yet played**. Per team: a few seekers who only fight, and
defenders and attackers split 51 to 57% toward the team's own side. Bots
pick an objective, hold `interact` at it to hack, undo or retake, guard a
teammate at work, and defend from posts round their own. The MAG body
describes Sabotage's uplinks A and B and data center C to the brain, and
runs a bot's interact through the same checks as a person's. Spec 14.8
has the design, the research and the numbers. **Limit:** most of
Sabotage's objectives are on upper floors or indoors, which the one-layer
map does not have. Bots can work A and B on kennicott and A on
remote_comms; nothing on cosmodrome; C nowhere. Where they cannot, they
fight round the objective. A person can still do all of it. (Since 15:03,
on the navigation mesh: A, B and C on cosmodrome and remote_comms, A and
B on kennicott; M11 below.) Same day,
after the owner watched a sandbox recording: a `locked` objective flag
(agreed by the owner; MAG locks A and B once C opens), objectives shared
out by weight so few wait at C before it opens, and sandbox spawn waves
as MAG's (defenders first at 20 s, attackers at 35 s). Deployed 11:57,
not played. Sample recordings: `medius-testserver/recordings/sabotage-*.bbrec`.

## 7. Work queue

In order. Each task says what kind it is:

- **routine**: follow the guide; the path is known.
- **needs the owner**: you cannot finish without them playing or answering.
- **research**: the path is not known. Find out, write down what you found
  and what you propose, and **stop for the owner's go-ahead before
  building.** A plausible guess built into code is the most expensive
  mistake available here.

### Finish the MAG bot

| # | Task | Kind | Done when |
|---|---|---|---|
| M1 | Read the owner's first real recording with them. List what looks fake, most visible first. | needs the owner | A written list in the journal, agreed with the owner. |
| M2 | Fix the turn-speed snap (CHANGING-BEHAVIOUR.md 2.5): ease in, overshoot and correct, by persona. | routine, then needs the owner | In a recording, a bot's turn-rate chart rises and falls like the person's in the same match; the turn histogram for bots lies near the people's. |
| M3 | Measure MAG's top turn rate and stick curve. Ask the owner to hold the look stick fully over for a few seconds, then half over, in a recorded match; read the rate off the viewer. Put the numbers in `Look` in `BotGame.cs`. | needs the owner | `Look` is measured, and the comment says from what. |
| M4 | Settle the two unknowns by asking: do bots aim toward you when above or below; can you see their gun fire. Fix `BotMath.MagPitch`/`BrainPitch` or the held-weapon fields if not. | needs the owner | Both answered and written in the journal. |
| M5 | People's names in recordings. Add a name to what the body sends about a player (the protocol has no field: propose one to the owner first). | research (small) | The viewer shows the owner's name, not "Player 3". |
| M6 | A settings file for behaviour, so tuning does not need a rebuild: the persona formulas and the main constants in CHANGING-BEHAVIOUR.md section 2, read at world start. | routine | Changing a value in the file changes the next match; defaults reproduce today's sandbox baseline exactly. |
| M7 | More bots. Raise `FillPerTeam` 8, 16, 32 with the owner playing each step. Watch the server log for update sizes; `BotsPerUpdate` in `MagGameComponent.Bots.cs` caps bots per message. | needs the owner | The largest count that plays cleanly is written down, with what breaks above it. |
| M8 | Crouch and jump, by ADDING-CAPABILITIES.md section 2. The MAG state numbers are in the server's player-state table (`data.PlayerStates`); `Jumpfall` is 11. | routine | Bots crouch when holding still under fire and can be seen doing it. |
| M9 | Maps for the other levels (section 4). One level at a time, smallest mode first: Sabotage (done 2026-11-17), then Acquisition, Interdiction, Domination. | routine per level, **research** when a picture looks wrong | Each level's PNG looks like the level, both sides connect, and the sandbox plays on it with few stuck recoveries. |
| M10 | Objectives. Sabotage done 2026-11-17 (spec 14.8; the person's path is `Objective` in `MagGameComponent.Objectives.cs`, the bot's `BotWork`). Next modes: Acquisition, Domination, Escalation, each after a look at its objectives (ADDING-CAPABILITIES.md 4). | **research** per mode | A findings note per mode first. |
| M11 | Every floor, upstairs and indoors: a navigation mesh (DotRecast). **Blocks Sabotage**: of its nine objectives bots can work three on the grid. The owner chose option C (navmesh) 2026-11-17. All five stages built and deployed 2026-11-17 15:21 ([docs/M11-LAYERED-MAP.md](docs/M11-LAYERED-MAP.md) section 6): Sabotage levels on mesh packages (version 4, points with floor heights); the viewer shows floors. Suppression's levels still grid. | needs the owner | The owner answers whether sver's outer hills are played; then Suppression packages rebuilt on the mesh. |
| M12 | Grenades, mounted guns, vehicles, in that order (ADDING-CAPABILITIES.md 3 and 5). | **research** | As M11. |

The owner decides when MAG is "finished". M1 to M7 make Suppression feel
right. M8 to M10 make the other modes playable. M11 and M12 are large.

### Then Far Cry 3

Far Cry 3 is a different kind of body from MAG. Do not copy MAG's. In MAG
the server moves the bots. In Far Cry 3 one player's PC hosts the match
and every player is a running copy of the game, so a bot is **a real copy
of the game driven through a virtual controller**, with the body living in
the shim (`UplaySDKShim/`) that is already loaded into the game. Section
11 of the spec writes Far Cry 3's modes out in protocol terms.

Everything below is research. Answer these in order and report after each
before going further:

| # | Question | Why it decides things |
|---|---|---|
| F1 | How many copies of Far Cry 3 can one ordinary PC run at once, at the lowest settings, each signed in as a different player? | This is the number of bots. If it is 2, say so before anything else is built. |
| F2 | Can the shim feed the game a controller? (The game reads XInput; the shim already hooks imports.) Prove it by making a character walk forward and turn at a set rate. | No input, no body. |
| F3 | Where in the game's memory are the players: position, facing, health, team, alive? Proved by logging them while the owner moves and comparing with what they did. | This is `OBSERVE`. |
| F4 | Damage, death and shot events: where can the shim see them? | This is `EVENT`. |
| F5 | Can a copy of the game get from launch into a match with nobody at the keyboard? (Sign-in is solved in the shim; joining a lobby is not.) | A bot has to arrive by itself. |
| F6 | The level's solid geometry and spawn points, out of the game's map files, into the soup format (ADDING-A-GAME.md section 4). | The map package. |
| F7 | **Brain work, needed before any real-client game:** each bot's copy of the game sees only part of the match, and connects on its own link. The brain today makes one world per link and cannot merge several links into one match (spec 14.3, "Worlds"). Design this and agree it with the owner. | Without it, each bot would be alone in its own world. |

F7 is the only one that changes the brain. It can be designed while F1 to
F6 are being answered.

### GTA IV

Not scheduled. The design for it, and for one game mixing racing, combat
and missions, is in the spec, section 11 ("Grand Theft Auto IV" and "One
match, several kinds of play"). It needs driving, which nothing has yet.

## 8. Traps that have already cost time

- **`MagReservation` is a record.** Its hash changes when its fields
  change. Never use one as a dictionary or set key without
  `ReferenceEqualityComparer.Instance`. This once made every bot look like
  a person to the brain, and the only symptom was bots standing still.
- **A buffer that grows.** `x.Encode(ref buffer)` may replace `buffer`.
  Call it first, then slice: `int n = x.Encode(ref buffer);
  buffer.AsSpan(0, n)`. Never `buffer.AsSpan(0, x.Encode(ref buffer))`.
- **Map width and body width must agree.** If the map builder thinks a bot
  is thinner than the code that moves it, bots wedge on every corner
  (9,897 stuck recoveries in one match before this was fixed).
- **Entity id 0 means "nobody"** in events. MAG's body sends the character
  index plus one.
- **MAG's yaw is not the brain's yaw.** MAG: 1024 steps a turn, 0 facing
  +Z, growing toward +X. Brain: radians, 0 along +x, counter-clockwise.
  All conversion goes through `BotMath.cs`. Do not convert anywhere else.
- **Havok index arrays in MAG's collision are 16-bit, four per triangle.**
  Reading them as 32-bit gives a plausible-looking wrong mesh.
- **MAG's terrain is the one collision resource the level file never
  names.** Leave it out and only the buildings are walkable.
- **The gun's numbers and the brain's aim are tuned together.** Bots hit
  mostly by aim, not by the gun: MAG's spread (read as degrees) is about
  as tight as the old fixed scatter. The shooting-range test is the check
  on both; live MAG bodies are wider (0.4 m) than the sandbox's (0.35 m),
  so live hit rates run higher than the sandbox's.
- **Decide can return without choosing a goal** (no path searches left
  this tick, or a path search failed). Whatever ends a goal's reason (a
  camp, a hunt) must also clear the goal itself, or the skill runs next
  tick with nothing to work from. With 63 bots this happens every match.
- **The MAG folder's `.slnx` builds the brain in Debug.** It does not
  list the brain's projects, so MSBuild builds them in their default
  configuration. Build `Plugin.MagGameServer/Plugin.MagGameServer.csproj`
  for anything you deploy (section 4).
- **Old maps go to the grid, new ones to the mesh.** `GridMap`'s height-taking
  questions (`FindPath(Vector3...)`, `ClearPath(Vector3...)`, `Standable`,
  `SnapToFloor`...) answer from the navigation mesh when the package has
  one. Use those in the brain, never the 2D grid ones, or a bot upstairs
  is asked about the ground floor. A version-2 package must reproduce the
  baseline exactly.
- **The in-process brain and the body share map packages.** `LoadedMaps` in
  `MagGameComponent.Bots.cs` hands the brain the body's loaded package; a
  test that changes `MapDirectory` or `RecordDirectory` between games gets
  the new one (both were once fixed at the brain's start).
- **Floors switched on from a spawn start under its feet.** `NavMap.KeepReachable`
  takes the floor at most 1.5 m below each start point; a looser search
  picked the ground under a platform and switched the platform off.
- **A body takes a drop link only when really stopped at the edge, heading
  nearly straight at it** (`NavMap.DropAt`, both bodies' walking). Stairs
  and ramps are lined with short drop links along their sides; with a
  looser rule, bots sliding along a stair's side stepped off it and walked
  back, again and again (seven defenders near their spawn on cosmodrome,
  found by the owner in a recording). Test: `A_bot_sliding_along_a_ramp_edge_does_not_step_off_it`.
- **Objectives sit where the map has no floor.** An objective's position
  is the thing itself (a console, a dish), often indoors or upstairs. The
  brain stands at the nearest walkable cell at that height within 12 m
  and logs "NOT workable" when there is none in reach. Read that log line
  before blaming the behaviour.
- **The shell refuses `rm -f $DIR/*.x`.** Write `rm -f "${DIR:?}"/*.x`.
- **Slow first use.** Loops in `BotBrain.Maps` that run mid-match are
  marked `AggressiveOptimization`, because the game server does not set
  the compiler option the brain's own programs set. A new hot loop there
  needs the same mark, or its first call in a match takes tens of
  milliseconds.
- **One slow tick about 14 s into a fresh process** (60 to 95 ms, in
  perception) is .NET tier-up, not the brain. It vanishes with
  `DOTNET_TieredCompilation=0`. Do not chase it in the bot code.
- **The sandbox's stand-in person does not move** on real maps. Its row in
  the viewer (0 m/s, 100% still) is not a bug in the viewer.
