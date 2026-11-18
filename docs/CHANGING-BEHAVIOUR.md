# Changing how bots behave

This is the working guide for tuning. It says where each kind of behaviour
lives in the code, what the numbers are today, and how to try a change
safely. The other guides: [adding things bots can do](ADDING-CAPABILITIES.md)
and [adding a game](ADDING-A-GAME.md).

**One thing to know first.** Every value below is a constant in C#. There
is no settings file for behaviour yet, so a change means editing the code,
rebuilding and deploying. Making the common ones loadable from a file is a
sensible next step and has not been done.

## 1. The loop

1. **Record** a match (`--record` in the sandbox, `Bots.RecordDirectory` in
   MAG's title.json).
2. **Look** at it in `viewer/index.html`. Select a bot and compare it with
   a person: the turn-rate chart, the speed chart, the turn histogram.
3. **Find** the behaviour in the table in section 2 and change it.
4. **Check** in the sandbox on the real level, with a recording:

   ```sh
   dotnet test BotBrain.slnx -c Release
   dotnet run --project BotBrain.Sandbox -c Release -- --agents 31 --humans 1 --seconds 180 \
       --map-file ../RE-Work/mag/v212/maps/<level>-<hash>.bbmap --record recordings
   ```

   The sandbox prints kills, hit rate, share of time standing still, stuck
   recoveries and time per goal. Those four catch most regressions. Use the
   same `--seed` before and after to compare like with like.
5. **Deploy** (section 4) and play.

The sandbox's bots only ever fight other bots. It tells you whether a
change broke something and what it did to the numbers. Only a recording of
a real match tells you whether it looks human.

## 2. Where each behaviour lives

All paths are under `BotBrain.Core/`. Nothing in this folder may know which
game it is playing.

### 2.1 Who a bot is: `Persona.cs`, `Persona.Generate`

Each bot gets a persona from a seed. `Skill` is drawn once (a bell curve
cut off at −2 and +2) and most hand-eye values follow from it. The rest are
drawn independently, so a skilled player can still be passive or wander off.

| Value | Today | What it does |
|---|---|---|
| `Reaction` | 0.26 s − 0.035 × skill | Typical time to notice an enemy that comes into view. |
| `PlanInterval` | 0.12 s − 0.015 × skill | How often the aim is re-judged. Between judgements the thumb carries on. |
| `AimTimeConstant` | 0.20 s − 0.03 × skill | How hard the bot pushes to close an aiming error. Lower is snappier. |
| `AimNoise` | 0.18 − 0.05 × skill | Misjudging how far to turn, as a share of the turn. |
| `AimJitter` | 0.004 − 0.001 × skill rad | Small constant aiming error. |
| `Tremor` | 0.015 − 0.004 × skill | Thumb unsteadiness, in stick units. |
| `ThumbSpeed` | 26 + 3 × skill | How fast the thumb reaches the position it is going to. |
| `Pursuit` | 0.6 + 0.12 × skill | How much of a moving target's motion is anticipated. |
| `TriggerSlop` | 1.6 − 0.3 × skill | How far off target the crosshair may be when the bot fires. 1 is on the body. |
| `Aggression` | 0.2 to 0.9 | Willingness to chase an enemy that went out of sight. |
| `Obedience` | 0.3 to 1.0 | How often it goes where its squad goes. |
| `StrafeHabit` | 0.3 to 1.0, + 0.1 × skill | How often it sidesteps in a fight. |
| `BurstDiscipline` | 0 to 1 | Shorter bursts at range. |
| `ScanHabit` | 0.3 to 1.0 | How much it looks around while moving or holding. |
| `MemorySeconds` | 6 to 14 s | How long it remembers where an enemy was. |
| `FovHalfAngle` | 0.62 to 0.78 rad (71 to 89 degrees wide) | Half the width of what it can see. Was 0.8 to 1.0 until 2026-11-16, which the owner found too wide. [guess] about what a console shooter's screen shows; not measured from any game. |
| `SpotDistance` | 45 m + 7 × skill, ±7, kept within 26 to 64 m | How far away a person is still easy to pick out (see 2.2, time to notice). |
| `Camper` | about 25% of bots (more among the less aggressive) 0.6 to 0.95, the rest 0 to 0.1 | Chance, on spawning or reaching a roam point, of going to camp (2.3). |
| `PreferredRange` | 12 to 35 m | The distance it tries to fight at. |
| `Grudge` | 0.05 to 0.7, + 0.2 × `Aggression` | Chance, after being killed, of going back for the killer once it respawns. |
| `Rush` | 0.1 to 0.6, + 0.4 × `Aggression`, − 0.5 × `Flank` | Chance (halved) of heading straight for distant gunfire it hears. |
| `Flank` | about 30% of bots 0.5 to 0.9, the rest 0 to 0.2 | Chance that a hunt goes round the side instead of straight at the enemy. |
| `FrontLine` | 0.3 + 0.4 × `Aggression`, ±0.08 | Where it likes to roam: 0 is its own spawn, 1 the enemy's. |
| `AimBias` | 0.007 − 0.0015 × skill rad, ±0.0012, kept within 0.003 to 0.011 | How far off (one sigma, each way) its idea of a target's middle is. Drawn for each new target, walked in while firing (2.4). An angle, so it hardly matters at 10 m and dominates at 80 m. |
| `TriggerFloor` | 0.012 − 0.002 × skill rad, ±0.002, kept within 0.006 to 0.018 | It fires once the crosshair is this close, even if the target is smaller than that (people do not wait for a perfect line-up far away). |
| `HoldFireRange` | 55 to 110 m | Beyond this it does not open fire (it closes in) unless camping or shot at in the last 4 s. |
| `Rotate` | 0.3 to 0.7 | Objective modes: when the objective it plays is lost (or won), the chance it goes to the team's other objective of its kind rather than the nearest one it has any business at (2.3). |

`Grudge`, `Rush`, `Flank`, `FrontLine`, `SpotDistance`, `Camper`, `AimBias`, `TriggerFloor`, `HoldFireRange` and `Rotate` come from a second random stream
(`style` in `Persona.Generate`), so adding them did not change any older
trait for a given seed.

Bot names come from the two word lists at the top of the same file.

To make the whole population better or worse, shift how `skill` is drawn.
To change one trait for everyone, change its line. A persona is not saved
between matches: the same seat and brain seed give the same persona, and
MAG seeds the brain from the clock.

### 2.2 What a bot knows: `World.Perception.cs`

A bot is sent the truth about everyone and this file withholds most of it.

| Behaviour | Where | Today |
|---|---|---|
| How far it can see | `ViewRange` in `World.cs` | 120 m |
| How far it hears gunfire | `HearRange` in `World.cs` | 60 m |
| How many enemies it keeps track of | `MaxTracks` in `World.cs` | 4 |
| What counts as seen | `Perceive` | Inside the field of view (or within 3 m), with a clear line to the chest or the head. |
| Time to notice | `NoticeDifficulty` | Drawn around `Reaction`, times: 1 + (distance / `SpotDistance`)² (twice as long at `SpotDistance`, five times at double it); 1 + (angle off centre / 26°)²; 2.5 for someone standing still, 1.4 walking, 1 running; ×1.6 crouched, ×3 prone. Someone firing (the `Firing` flag, or a shot heard from them in the last second): the distance term drops to 0.3 of itself and movement counts 0.6. The target has to stay in view the whole time or noticing starts over. Before 2026-11-16 distance and the edge of view added at most +50% each, and bots picked people out at 110 m. |
| Noticing someone it remembers | `Perceive` | 0.6 × the normal time. |
| Forgetting | `Perceive` | After `MemorySeconds` out of sight. At once if they die. |
| Being shot | `OnDamage` | Knows the direction within about 15°, and guesses the distance to within ±40%. |
| Hearing a shot | `OnShot` | One bot in four within range reacts, with an 8 m error in where it was. Bots already fighting ignore it. |
| Hearing distant gunfire | `HearFarGunfire` in `World.Roaming.cs`, `FarHearRange` | Out to 160 m, each bot at most every 2 s, if it is not fighting, investigating or hunting. Error 3 m + 8% of the distance. Goes to it with chance `Rush` / 2. |
| What the team knows | `AddContact` in `World.Roaming.cs` | Every enemy a bot sees, every alert (hit or heard) and every teammate's killer becomes a contact for the bot's team: a place, fading over 25 s, forgotten after 60 s, at most 48. Contacts within 15 m merge. Shared by the whole team, standing in for callouts and the kill feed. |
| How often it looks | `World.Tick` | Every 3rd tick at full effort, every 6th otherwise. |

### 2.3 What a bot decides to do: `World.Decision.cs`

`Decide` runs for each bot about four times a second, and at once when
something changes. It picks one goal:

1. **Fight** if any enemy is visible. The nearest one, except that the
   current target counts as 40% closer than it is, so the bot does not
   switch on small differences.
2. Otherwise compare **chasing** a remembered enemy, **hunting**, and
   **the objective**:
   - chase = (0.35 + 0.5 × `Aggression`) × how fresh the memory is;
   - hunt = (0.6 + 0.4 × `Grudge`) for revenge or (0.5 + 0.4 × `Rush`) for
     gunfire, times 0.5 to 1 as the hunt's time runs out (see below);
   - objective = 0.45 + 0.3 × `Obedience` with zones to hold, or 0.45 when
     roaming;
   - whichever the bot is already doing is multiplied by 1.15.
3. With an objective: **go to** its zone, then **hold** inside 70% of the
   zone's radius. With none: **roam** (below).

**Roaming** (`World.Roaming.cs`). A roam point is drawn at random by weight,
not evenly. For each team, `LayoutFor` works out how far each point is out
of the way between its spawns and the enemy's (walking distances, from the
route fields the points already have) and how far forward it is. Weight =
route × (0.15 + 0.85 × closeness to the bot's `FrontLine`) × (1 + 4 × heat),
where route falls off past 25% longer than the direct way (`RouteSpread`)
and heat is how close the team's recent contacts are (35 m reach). Squads
use front line 0.5. Before this, choice was even, and on MAG's levels most
points were on the empty rim of the play area (see 2.8).

**Camping** (`World.Camping.cs`). A bot with a high `Camper` may, on
spawning or on reaching a roam point, pick a spot to camp at: one of the
map's vantage points (2.8) that no teammate holds, weighted toward a little
behind its `FrontLine` and toward nearer ones (with objectives: near the
zone it was sent to; not before it has been sent to one). It walks there,
then stands still, facing the direction from the spot that sees the most
open ground 10 to 50 m out and leans toward the enemy's side (worked out
once per team at match setup, 24 directions). It looks about within ±50°
of that every 1.2 to 3.5 s and over its shoulder one look in twenty. It
stays 40 to 120 s × (0.5 + `Camper`). It leaves early if it is shot by
someone it cannot see, or killed there (that spot is then avoided for 90
s), or stuck three times on the way. Then no new camp for 15 to 40 s.
Score in `Decide`: 0.55 + 0.35 × `Camper`, so a camper mostly stays put
rather than chasing what it heard. Fighting from the spot it does not
close in and sidesteps a third as often. A map with no vantage points
falls back to its interest points, weighted by route.

**Hunting.** A hunt is a trip to a place where an enemy is believed to be:

- *Revenge.* When a bot dies, whoever hurt it in the last 4 s is its killer
  (MAG's body sends damage but no death events, so death is read from the
  alive flag). With chance `Grudge` it hunts where the killer stood, ±4 m,
  for 60 to 100 s after respawning.
- *Gunfire.* Distant gunfire (2.2), for 30 to 50 s.
- *Flanking.* With chance `Flank`, a hunt first goes to a point to one side
  of the line to the target: 45% of the distance (20 to 45 m) out, 40% of
  that short of it. Then on to the target.

A hunt ends on arrival, when its time is up, when it is stuck three times
running, or when a visible enemy or a fresher memory wins in `Decide`. Legs
over 50 m (`HuntSearchRange`) follow the route field of the roam point
nearest the leg's end, then a route search for the last stretch. The last
30 m of the final leg are walked, eyes on the target.

**Objective roles** (`World.Objectives.cs`; the design and the research
behind it are in ATTACH-PROTOCOL.md 14.8). Only with `interact`
objectives. Each team draws a plan once a match:

| Value | Today | What it does |
|---|---|---|
| `SeekerShareLow`, `High` | 6 to 14% of the team | Seekers: play for kills, ignore objectives (they roam and hunt as in a match with none). [guess] The owner's "a small number". |
| `MajorShareLow`, `High` | 51 to 57% of the rest | The team's natural side (defending for the team that starts with more objectives). Whole-number targets keep it a majority, never level. The owner's numbers. |
| `RoleWeight` | stop 3, guard 1.6, hold 1 (defenders), take 1 (attackers), calm 0.25 (defenders) | How much a role wants an objective in each stance. |
| `FallbackWeight` | defenders take 1; attackers hold 1, calm 0.25 | With nothing of its own kind anywhere, the other kind's business. |
| `ChooseSite` weighting | × 1 / (1 + teammates on it / its share) × 1 / (1 + walking distance / 150 m); the current one × 4. Its share = role players × its weight / all weights. | Spread over objectives in proportion to how much each matters (C before it opens gets a few), nearer likelier, sticky. One the map cannot stand anyone at (not workable) × 0.3 when it wants working. |
| Chain | × 2 for an objective that requires others, when it can be taken or must be held | The end of a chain (Sabotage's C) is what the rest were for. |
| Locked | an objective the body flags locked has no weight for anyone | MAG locks A and B once C opens. |
| Review | 0.4 to 2.5 s after any objective changes; every 35 to 70 s anyway | When bots think again about which objective. |
| `SitePull` (score in `Decide`) | (0.5 + 0.3 × `Obedience`) × 1.6 stop, 1.25 guard, 0.8 calm, 1 otherwise | Against chase, hunt and camp. A hunt leading over 60 m (`FarFromSite`) from its objective is scored at 0.35 of itself. |
| `GiveUpWorkSeconds`, `RefusedSeconds` | 12 s, 240 s | Holding interact that long with nothing changing: the team leaves it until it next changes, or 240 s. |
| Camping | attackers at 40% of `Camper`; spots weighted toward the objective (35 m) | Defenders camp near what they defend. |

Squads (`AssignSquad`, `UpdateSquads`, `SetSquadZone`): eight to a squad.
Once a second squads are spread over the zones, two on a zone the team does
not hold for every one on a zone it does. A bot ignores the order and picks
the nearest zone it does not own with probability (1 − `Obedience`) / 2.
When roaming, a squad moves to a new point every 40 to 90 s, or after 8 s
if the team's contacts are somewhere else (the hottest point has heat 0.6
or more and the squad's has under a fifth of that). Obedient members follow
the squad's point; each bot picks its own when it arrives
(`PickRoamTarget`).

### 2.4 How a bot carries a goal out: `World.Skills.cs`

| Skill | Behaviour | Today |
|---|---|---|
| `Engage` | Where it aims | At the chest plus its held error (`AimBias` × distance). While the trigger is held the error shrinks by e every 1.5 s (`AimWalkInSeconds`); a new target, or 2 s away from this one, draws a new error. |
| | When to fire | Crosshair within 0.45 m sideways and 0.7 m up or down of where it aims, times `TriggerSlop`, or within `TriggerFloor` if that is wider. Not beyond `HoldFireRange` unless camping or shot at in the last 4 s. |
| | Bursts | Held for 1.5 s inside 15 m. 15 to 40 m (`LongRange`): 0.8 s − 0.5 × `BurstDiscipline`, then a 0.12 to 0.35 s pause. Beyond 40 m: 0.12 to 0.37 s (2 to 4 rounds), then 0.3 to 0.7 s to re-aim. |
| | Sidestepping | A new choice every 0.4 to 1.4 s: left, right, or stand, by `StrafeHabit`. |
| | Range | Closes in when beyond `PreferredRange`; backs off inside 6 m. |
| | Lost sight | Keeps aiming where they were until the next decision. |
| `Travel` | Speed | Always sprints, except within 30 m of a spot it is investigating. |
| | Eyes | Looks where it is going, with a glance aside (about ±35° × `ScanHabit`) lasting 0.4 to 1.0 s every few seconds. Smaller while sprinting. |
| | Route | Steering point refreshed every 0.25 s. |
| | Following a route search (investigate, hunt) | Skips ahead to the furthest of the next three waypoints it can walk straight to. If the next waypoint is out of sight (it cut the corner it just rounded), it goes back to that corner; if that is out of sight too, it searches again from where it stands. |
| `CheckStuck` | Stuck | Less than 0.5 m of progress in a second: step sideways for half a second and re-route. A route search is thrown away and done again from here. Stuck three times running: a hunt or an investigation is given up. |
| `Hold` | Position | A new spot inside the zone every 3 to 9 s, walked to at 70% speed. |
| `PlaySite` (World.Objectives.cs) | Working | Something to take or stop and nobody at it: walk (no sprint inside 12 m) to the stand spot, then hold `interact`, creeping onto the spot at 30% speed. Eyes on the work, a 0.4 to 0.9 s glance round 30% × `ScanHabit` of the time. |
| | Guarding a worker | A post 3 to 10 m out (`GuardInner`, `GuardOuter`). |
| | Defending | A post 6 m to 10 + 12 × `Aggression` m out, facing where the team last met the enemy within 90 m, else the enemy's spawns (`ThreatYaw`); a new post every 6 to 18 s; one in a straight, clear walk from the objective. Further off than the post + 10 m: travel back. |
| `LookAround` | Scanning | Turns to a new direction every 1 to 3 s (faster with `ScanHabit`). |
| `RunSkill` | Reloading | Tops up below 18 rounds when not fighting; reloads when empty in a fight. |

### 2.5 The hands: `RunMotor` in `World.Skills.cs`

This is where "does it look like a thumb on a stick" is decided.

- Every `PlanInterval` (±30%) the bot judges how far off its aim is, with
  `AimNoise` and `AimJitter` added, and turns that into a wanted turn rate:
  error ÷ `AimTimeConstant`, plus `Pursuit` × how fast the target is
  crossing its view.
- `StickFor` turns the rate into a stick position by inverting the game's
  own dead zone and response curve from the manifest. The rate is capped at
  the game's top turn rate.
- The stick does not jump there. `Spring` moves it like a damped spring at
  `ThumbSpeed`, and `Tremor` shakes it, more the further it is pushed.
- The move stick follows what the skill wants with a 0.07 s lag.
- The trigger goes down 0.05 to 0.12 s after the bot wants to fire, and
  comes up 0.03 to 0.1 s after it wants to stop.

**Known problem (2026-11-16).** A bot that spots an enemy to one side goes
to the game's top turn rate and stays pinned there until it arrives. A
person eases in and usually overshoots. The cause is in the first bullet:
any error larger than top rate × `AimTimeConstant` (about 44° at 220°/s and
0.2 s) asks for more than the top rate, so the stick sits on its stop. This
is the first thing to tune and nothing has been changed yet.

### 2.6 How much effort a bot gets: `UpdateFidelity` in `World.cs`

A bot runs at full effort while a person is within 30 m, or within 150 m
with a clear line to it, or is its target, and for 2 s after. Otherwise its
hands update every third tick and it looks half as often. This is only
about cost. If bots look worse at a distance, check here first.

### 2.7 Limits on cost: `BrainOptions` in `World.cs`

`MaxPathRequestsPerTick` (2) and `MaxPathNodes` (6000) bound route
searches. A bot whose chase would need a longer search gives up the chase.
Raising them makes distant chases more likely and slow ticks possible.

### 2.8 Where roam points and camp spots are: the map builder

The map tool places 16 interest points (`MeshMapOptions.InterestPoints`) as
far apart as possible, but only where walking from one side's spawn to the
other's through that place is at most 35% longer than the direct way
(`InterestRouteSlack`) and at least 20 m from either spawn
(`InterestSpawnClearance`). Distances are walked outward from each spawn
(`GridMap.DistancesFrom`), because spawn platforms can be left and not
climbed back onto. Without spawns of two sides it spreads over everything,
and the tool says so.

It also finds 12 vantage points (`VantagePoints`) for campers: from cells on
the same routes, every 3 m, it counts how much of the routes (a sample
every 6 m, 10 to 70 m away) it can see from eye height to chest height, and
adds up to 36% for solid things within 1.2 m (something at its back). Spots
walled in on six sides or more are refused. The best, at least 25 m apart,
are kept. About 30 s a level, using every core.

Changing these means rebuilding the map packages.

### 2.9 Things that are the game's, not the brain's

The body decides how fast a character moves and turns and what a bullet
does. For MAG these are constants at the top of
`Plugin.MagGameServer/Battle/BotGame.cs`:

| Value | Today |
|---|---|
| `RunSpeed`, `SprintSpeed` | 4.2 and 5.8 m/s (measured from players: 4.17 and 5.82) |
| `Look` | 220°/s turn, 140°/s pitch, dead zone 0.1, curve exponent 2. **Guessed, not measured from MAG.** |
| `Magazine` | 30 rounds, whatever the gun |
| Fire rate, bullet spread | The bot's gun from MAG's weapons.lua (`WeaponSpread.cs`, `Data/<build>/mag-weapon-spread.json` by `tools/re/weaponspread.py`): its refire time, and a cone that widens with each shot and while moving and closes again. How MAG uses those fields was not read from its code: the reading is a [guess] written out in `WeaponSpread.cs`. An unknown gun shoots like an M4A1. |
| Reload time | 2.4 s, in `Weapon` |
| Objective reach | 2.5 m across, 3 m up or down (`BotObjectives.Reach`, `ReachUp`). [guess] The client decides a person's reach; the server does not check it. |
| Objectives sent | Sabotage only: uplinks A and B, then C requiring both (`BotObjectives.cs`, which also maps MAG's objective states to owner and progress). |

If bots turn faster or slower than players can, fix `Look` here. The brain
plans its stick movements from these numbers, so wrong numbers make every
bot wrong in the same way. How many bots join is `Bots.FillPerTeam` in
title.json, or the game type's whole team (32 a side in Suppression) with
`Bots.FillToTeamSize`. Bots arrive when the first person connects to the
game (while they load). A person placed on a team takes a bot's seat
within a second (a dead bot, else the newest); a team left short is
topped up after 10 s, four bots a second (`RefillDelayMs`, `RefillBatch`
in `BotGame.cs`).

## 3. Rules for a change

- **No game knowledge in `BotBrain.Core`.** If a behaviour depends on the
  game, the game's body puts a number in the manifest and the brain reads
  it.
- **Use the bot's own random source** (`a.Rng`), never `Random` or the
  clock, so a match with the same seed plays out the same way.
- **Vary by persona, not by constant,** where people differ. A constant
  makes every bot do the same thing at the same moment, which is the most
  visible kind of fake.
- **Record what you add.** If a new decision or belief matters for
  understanding a bot, write it in `World.Recording.cs` and draw it in the
  viewer, or the next tuning pass is blind to it.
- **Watch the cost.** The sandbox prints time per tick by phase. 256 bots
  must stay well inside 33 ms.

## 4. Deploying a change to the MAG test server

The brain is compiled into the game server plugin's output.

```sh
dotnet test  Plugin.MagGameServer -c Release
dotnet build Plugin.MagGameServer/Plugin.MagGameServer/Plugin.MagGameServer.csproj -c Release
```

Build the **project file** before deploying, not the folder: the folder's
`Plugin.MagGameServer.slnx` does not list the brain's projects, and
MSBuild builds a reference outside the solution in its default **Debug**
configuration. Every deploy until 2026-11-17 11:24 shipped an unoptimised
brain that way. Check: the deployed `BotBrain.Core.dll` should be byte for
byte the one in `BotBrain/BotBrain.Core/bin/Release/net10.0/`.

Then copy from `Plugin.MagGameServer/Plugin.MagGameServer/bin/Release/net10.0/`:

- `Plugin.MagGameServer.dll` and `.pdb` to `medius-testserver/Plugins/`
- `BotBrain.Protocol.dll`, `BotBrain.Maps.dll`, `BotBrain.Core.dll` to
  `medius-testserver/Dependencies/`

If the settings class changed, also `Plugin.Medius.dll` and `.pdb` from
`Plugin.Medius/Plugin.Medius/bin/Release/net10.0/` to `Plugins/`.

If the server is running, do not copy over a file it has loaded. Copy to a
new name in the same folder and rename it over the old one; the running
server keeps the old file and the new one loads at the next start.
