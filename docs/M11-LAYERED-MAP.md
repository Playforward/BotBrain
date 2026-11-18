# M11: maps with more than one floor (proposal, 2026-11-17)

Status: **the owner chose C (2026-11-17, "Straight to C").** All five
stages are built (section 6) and deployed (last at 15:21) for the three
Sabotage levels. Suppression's levels stay on grid packages until the sver
hills question is answered.

## 1. The problem

A map package holds one walkable floor per 0.5 m cell (ATTACH-PROTOCOL.md
9.1). The map tool floods out from the spawn points, and wherever two
floors stand over the same spot, the one the flood reaches first takes the
cell and the other does not exist for bots. Bots cannot go upstairs,
under a walkway, or into a room whose floor sits under a reachable roof.

Sabotage shows it most. Of its nine objectives bots can work three
(ATTACH-PROTOCOL.md 14.8). But it is everywhere:

| Level | Columns with somewhere to stand | With more than one floor | Walkable in today's package |
|---|---|---|---|
| cosmodrome (Sabotage) | 423,984 | 33% | 316,924 |
| kennicott (Sabotage) | 538,895 | 28% | 444,295 |
| remote_comms (Sabotage) | 1,077,399 | 25% | 731,284 |
| sver (Suppression) | 523,281 | 30% | 367,631 |

"Somewhere to stand" counts every level surface with 1.7 m of head room,
roofs nobody can reach included, so it is an upper bound. A person who
goes upstairs in Suppression is where no bot can follow today.

## 2. What was checked

**Are the objective floors connected, or cut off?** The cosmodrome,
kennicott and remote_comms packages were built again with extra seeds
placed on each objective's own floor, so the flood starts there too:

| Objective | Spawns only | Plus seeds on the objective floors |
|---|---|---|
| cosmodrome A (126 m) | no floor at its height | reached: 209 m walk from the attackers' spawn, 190 m from the defenders' |
| cosmodrome B (126 m) | no floor | reached: 304 m and 274 m |
| cosmodrome C (115 m) | no floor | reached: 313 m and 140 m |
| remote_comms B (32 m) | no floor | reached: 298 m and 361 m |
| remote_comms C (58 m) | no floor | reached: 316 m and 222 m |
| kennicott C (73 m) | no floor | no floor |

So those floors join up with both teams' spawns by stairs and doors the
tool already handles. They were lost only to the one-floor rule. And the
seeded builds hold 4 to 6% fewer cells in all (cosmodrome 316,924 →
298,012): the objective floors won some cells by taking them from
another floor. That is the one-floor rule at work.

**kennicott C is a different problem.** The level's collision, as
`collision.py` exports it, has no surface at the console's height at all.
The lowest thing above it is the roof, at 88.8 m against the console's
72.7 m. The data center's interior floor is missing from the export. It is
probably one of the six resources the exporter never places, or interior
geometry kept elsewhere [guess]. That is MAG-side research, separate from
this.

## 3. Options

### A. Objective floors first (stop-gap)

The map tool seeds the flood on each objective's floor before the spawns,
so where floors overlap, the objective's wins.

- Fixes cosmodrome A, B, C and remote_comms B, C: 8 of Sabotage's 9
  objectives workable, everything but kennicott C.
- Costs 4 to 6% of walkable ground elsewhere, mostly the floor under or
  over each objective room. Bots cannot use that floor, and may path
  round it.
- Small: the map tool gets an `--objectives <file>` option. No brain,
  body or format change. Hours, not days.
- Does nothing for upstairs anywhere else.

### B. A layered grid (recommended)

Keep the grid and everything built on it, but let a cell hold several
floors.

- **Package, version 3.** Per cell: how many floors and where they start
  in a flat list. Per floor: height, and for each of the eight
  neighbours, *which of that neighbour's floors* it links to (or none).
  One-way drops stay as they are. Version 2 packages keep loading as one
  floor per cell.
- **Map tool.** The flood works on (cell, floor) instead of cells, with
  today's step, head-room, probe and drop rules unchanged. Every floor it
  can walk to is kept. Pictures: the top floor as today, plus one picture
  of the cells that have more than one floor.
- **GridMap.** Routes, flow fields, `Move`, `GroundAt`, `IsBlocked`,
  `ClearPath` and `NearestFree` work on (cell, floor). Calls that have a
  position also take its height, and the floor is the one nearest below
  the feet. About 35 call sites in the brain and 5 in the MAG body. Sight
  and shots already use the triangles in three dimensions and do not
  change.
- **Brain.** An agent's floor comes from its entity's height, which the
  body already sends. Interest points, camp spots, objective stand spots
  and posts get a floor. Perception does not change.
- **Bodies.** MAG's `Walk` and the sandbox's move with the floor. Nothing
  on the wire changes: positions are already three-dimensional.
- **Viewer.** It draws the floor each player is on. The map from above
  shows the top floor, with a toggle to fade the others.
- **Size and speed.** About 1.3 to 1.5 times today's cells (measured
  above). Flow fields and route searches grow by the same factor.
  Packages grow from about 7 MB to about 10 MB.
- **Checked by:** a new two-storey test map where a bot walks upstairs to
  an objective; the Suppression baseline staying close; Sabotage
  objectives workable on all three levels (but kennicott C); and you
  watching a bot go upstairs in a recording.

### C. A navigation mesh (DotRecast)

Replace the grid with Recast/Detour polygons (DotRecast, zlib licence,
already listed in the spec's section 13.1).

- Handles floors, slopes and stairs well, and later ladders and jumps as
  links.
- But everything built on the grid would be rewritten: flow fields (which
  roaming, hunting, camping and objectives all use), the map tool's
  interest and vantage analysis, the brain's movement checks, both bodies'
  movement, the package format and the viewer.
- Several times the work of B, for gains nothing so far needs.

## 4. Recommendation

**B**, with **A first** if you want Sabotage playable sooner. A is only a
map-tool option and is thrown away when B lands. B fixes upstairs in every
mode, not just objectives. C costs too much for what it adds today.

Separately, a MAG-side research task: find kennicott C's missing interior
floor in the level export.

Neither A nor B touches the protocol or the design in AGENTS.md section 2.
B changes the map package format (version 3), and old packages keep
working.

## 5. Which pays off further down the road (asked 2026-11-17)

The owner noted the brain uses far less processor time than expected
(1 to 3 ms a tick for 64 players). So speed does not decide this. What
does:

- **Size.** A grid costs memory by area, a navigation mesh by how much
  there is in it. A 0.5 m grid of one MAG level is 0.3 to 0.7 million
  cells, about 7 to 12 MB. GTA IV's city is several kilometres across:
  a grid of it, every floor, would be hundreds of millions of cells
  [estimate]. Recast builds and loads such worlds in tiles.
- **Links.** The design (section 9 of the spec) wants typed links: jump,
  climb, ladder, vault, swim, drive. A navigation mesh has off-mesh
  links for exactly that. A grid can fake them only one cell at a time.
- **Floors, slopes, stairs.** A mesh handles them as a matter of course;
  B adds floors to the grid but stays a grid.
- **Different bodies.** Recast builds a mesh per body size (a soldier, a
  car), which driving in GTA IV will need.

So **C pays off further down the road**: Far Cry 3's and GTA IV's maps,
ladders, vehicles. B pays off now and keeps today's code. Option A works
with either as a bridge.

If C: build it in steps, with the brain's code calling the same questions
it asks the grid today (route, flow field, walk, ground height, line of
sight), answered by the mesh, so the brain changes little. Flow fields
become distances over mesh polygons. The map tool keeps writing a picture
per level. Old packages keep loading until every level is rebuilt.

## 6. Building C, in stages

Each stage is checked before the next starts. The brain keeps working on
the grid until stage 2 switches it over, so nothing on the test server
changes before then.

**Stage 1 (built 2026-11-17): the mesh in the map tool and the package.**

- `BotBrain.MapTool/NavMeshBuild.cs` builds it with DotRecast 2026.3.1:
  every triangle turned to face up first (the level's collision is not
  wound consistently, and Recast drops faces that point down); a body of
  0.25 m radius, 1.8 m tall, 0.45 m steps, 50° slopes, 0.25 m cells,
  64-cell tiles; then one-way drop links of up to 4 m (Recast's jump-link
  tool, climb-down only: bots do not jump yet); then the mesh again with
  the links in it. At 0.4 m radius a defenders' spawn on kennicott was
  walled in (Recast narrows the floor by the radius, rounded up to cells,
  so one-metre doorways closed).
- Polygons nobody can walk to from a spawn (roofs, sealed rooms, the hills
  outside) are switched off (`NavMap.KeepReachable`).
- `BotBrain.Maps/NavMap.cs` holds it, answers "the floor here" and "how
  far to walk there", and reads and writes it. The package becomes
  version 3 when it has a mesh; without one it stays version 2 byte for
  byte, so old hashes hold.
- The map tool reports how many roam points each team's spawn reaches and,
  with `--objectives`, each objective's floor and each team's walk to it.
  It writes `<level>.floors.png`: every floor in use, places with a second
  floor 2 m or more apart in violet, drops in orange.
- Tests: `NavMapTests` (a two-storey building with a ramp and an
  unreachable roof; the package round trip).

| Level | Polygons in use | Drops | Built in | Roam points reached | Sabotage objectives on a floor, both teams reach |
|---|---|---|---|---|---|
| kennicott | 11,024 of 13,655 | 14,600 | 9.8 s | 16 of 16 | A, B (C: no floor in the export) |
| cosmodrome | 10,460 of 15,775 | 12,467 | 10.1 s | 19 of 19 | A, B, C |
| remote_comms | 15,760 of 18,363 | 14,755 | 17.5 s | 16 of 16 | A, B, C |
| sver (Suppression) | 3,266 of 16,642 | 12,187 | 11.6 s | 16 of 17 | – |

Packages are about 10 MB (16 MB for remote_comms), in
`RE-Work/mag/v212/maps/v3/`. **Not deployed**: the brain on the server
reads version 2 only.

To look into: on sver the mesh covers the walled compound but not the
hills outside it, which the grid reached (one roam point is out there).
Either the grid crossed something a body cannot (a gap, a ledge) or the
mesh misses a way out. Worth asking the owner whether those hills are
played in MAG.

**Stage 2 (built 2026-11-17): the brain, and the sandbox's body, on the mesh.**

- `GridMap` asks with heights: `BuildFlowField(Vector3)`, `FindPath(Vector3,
  Vector3, List<Vector3>)`, `ClearPath`, `OpenLine`, `Standable`,
  `OnFloor`, `SnapToFloor`, `FloorHeight`. With a mesh in the package they
  go to `NavMap`; without one, to the grid exactly as before.
- `FlowField` is now a base with a grid version and a mesh version. On the
  mesh it is a shortest-distance tree over polygons (built in about 5 ms a
  target), and a body steers at the first corner of Detour's straight-line
  route down that tree's corridor, taking a drop link when the route goes
  down one.
- The brain passes heights everywhere: each agent's feet, each target's
  floor (zones, roam points, objectives, hunts, the killer's spot, the
  gunfire's). `Agent.Path` holds waypoints with heights.
- Objectives: the stand spot is the mesh floor under the thing, within
  12 m; workable within the body's reach as before.
- The sandbox body moves on the mesh (`NavMap.Move`, along the floor it is
  on) and drops down a link at an edge (`NavMap.DropAt`); `--objectives`
  checks a worker's height too.
- Checked: the Suppression baseline on a version-2 package is unchanged to
  the last digit (128 kills, 43.1%, 10% still, 7 stuck). A new test has a
  bot climb a ramp to an uplink on an upper floor and hack it. Sabotage on
  cosmodrome with the mesh (63 bots, seed 7) played through: A hacked at
  87 s, B fought over five times, both locked at 272 s, C armed at 299 s,
  defused, armed again at 321 s, blown at 366 s: the attackers won. On the
  grid nothing there could be worked. 307 stuck recoveries in 600 s (209 on
  objectives), against 143 on kennicott's grid: to watch. Brain 1.0 ms a
  tick. Recording: `medius-testserver/recordings/sabotage-syr-darya-navmesh-sandbox.bbrec`
  (the viewer still draws the grid's map: upstairs shows as black, stage 5).

**Stage 3 (built and deployed 2026-11-17 15:03): the MAG body on the mesh.**

- `BotGame.Spawned` puts a bot on its spawn's own floor (a platform, an
  upper storey); `BotGame.WalkOnMesh` moves it along the floor it is on,
  and at an edge with a drop link the way it is going it steps off and
  falls under gravity to the floor below, over several ticks (a test runs
  a bot off a 2.5 m platform: it lands beyond the edge, never more than
  0.6 m in one tick). Grid packages walk as before.
- `NavMap.KeepReachable` now starts from the floor under each spawn (1.5 m
  down at most), not merely the nearest floor: a spawn on a platform had
  started from the ground below it in a test, switching the platform off.
  The packages were rebuilt; their numbers barely moved.
- The in-process brain finds maps among the packages the bodies loaded
  (each in memory once), else in the map folder as set now, and reads the
  recording folder afresh for each match: both used to be fixed when the
  brain started.
- The bot integration test runs on the grid and on a mesh package.
- Deployed: `Plugin.MagGameServer.dll`, the brain, `DotRecast.Core.dll`
  and `DotRecast.Detour.dll` in `Dependencies/`, and version 3 packages for
  kennicott, cosmodrome and remote_comms (their version 2 packages moved to
  `maps/superseded/`). Not played.

**Stage 4 (built and deployed 2026-11-17 15:21): the map tool's points on the mesh.**

- `BotBrain.MapTool/NavPoints.cs`: for a package with a mesh, roam points
  and camp spots are found on the mesh, by the grid's rules: places (the
  polygons in use, one per 2 m cube so floors 2.5 m apart count apart),
  walking distances out from each side's spawn over the polygon graph
  (`NavMap.DistancesFrom`), the band of places on the routes between them,
  roam points spread over it (a floor up counts three times its height
  apart), camp spots by how much of the routes they see 10 to 70 m away
  through the level's triangles, with cover.
- Points carry the height of their floor: package **version 4** (reads 2
  to 4). The brain uses it (`GridMap.PointAt`) for roam targets, camp
  routes, a camp spot's view (looking down from a balcony at the street
  counts), arriving at a camp spot (not from under it), and the roam
  layout from the spawns.
- Result: every roam point reachable from both sides on all four levels.
  Camp spots mostly stay at street level (the long views are outdoors):
  above the ground on kennicott 1, sver 2, the others 0. Some roam points
  are upstairs (cosmodrome near uplink B).

**Stage 5 (built 2026-11-17): the viewer shows floors.**

- Recordings on a mesh map draw the map from above from the mesh's top
  floors, and add block 9: where a lower floor lies 2 m or more under the
  top one. Map points carry their height.
- `viewer/index.html`: places with two floors tinted violet; a **Floors**
  button (all, upper, lower) shows the chosen floor's map and fades players
  on the other one; with all floors, players on a lower floor get a dark
  dashed ring; the player panel says how high the feet are and on which
  floor. Recordings of grid maps show as before (no button).
- Sample: `medius-testserver/recordings/sabotage-syr-darya-navmesh-sandbox.bbrec`
  (Syr Darya, 63 bots and a person sitting still, 600 s, attackers won at
  296 s).

**Fixed after the owner watched the stage 5 recording (2026-11-17):** a
couple of defenders stuck near their spawn on cosmodrome for minutes. Seven
of them looped at the foot of a staircase: its side is lined with short
drop links (0.6 to 2.6 m), and a body sliding along that side, nudged a
centimetre by the mesh's edge, counted as blocked, found a drop link
roughly ahead and stepped off, then walked back. Now a drop is taken only
when the body made less than half its step and the link goes within about
45° of its heading. Same match again (seed 7, 600 s): stuck recoveries
283 → 93, standing still 28% → 14%, none left at the stair, kills 395 →
510 (the defenders get to the fight). The sample recording was replaced.
Deployed 2026-11-17.

**Fixed after the owner's next look (2026-11-17, deployed 16:05):**

- *Defenders from the top spawn on cosmodrome went a long way round to C*
  (370 m walked for 101 m straight). A 2.9 m drop toward C off the spawn
  side lands 1.5 m out, past a wall top no body can stand on; Recast's
  jump-link tool looked for landings closer in and made no link, so the
  route climbed through an upper storey instead. The map tool now makes
  drop links itself by the grid's rule (`NavMeshBuild.AddDrops`): along
  every outer edge, a floor 0.6 to 4 m below, 0.6 to 2 m out, the way
  across clear at 0.5, 1 and 1.5 m and the way down clear. Now 120 m. All
  packages rebuilt.
- *Attackers guarding A and B faced the wrong way* (on every map). A guard
  looked straight out from the objective to its post, and posts were
  spread all round it; where the enemy came from leaned on old fights
  near there, which for attackers were their own way in. Now the enemy's
  approach is where their walking routes from their spawns arrive, about
  15 m out (`World.Objectives.Approaches`, found once per team and
  objective), plus fights in the last 20 s; posts lean to that side and
  guards face it from where they stand. Attackers at a captured uplink
  facing within 60° of the nearest defender within 60 m: 24% before (worse
  than random), 47% after.
- *The match ends when C goes*: that is MAG's rule, not a fault. The
  defenders start with 1000 points; taking both uplinks completes a hidden
  objective worth 600 of them (the script's `dummy_c`), and C's
  destruction costs the last 400, so the attackers win. The sandbox now
  stops five seconds after its final objective goes, as a real match would.

Syr Darya again (seed 7, 900 s): the defenders now reach C fast enough to
hold it: armed three times, defused three times, not won in 15 minutes
(MAG's 20-minute clock would give it to the defenders).
