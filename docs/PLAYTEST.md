# Real-game checklist (2026-11-17)

Everything below is deployed on the test server (last at 15:03) and has
not been played. Restart ServerApp to load it. Tick what you see; for anything that
looks wrong, note the time in the match and the map, and say what you saw.
Every match is recorded to `medius-testserver/recordings/`; tell me which
file and I'll read it.

Known not to work yet, so don't spend time on them: bots going upstairs in
**Suppression** (its maps are still the old one-floor kind, see the hills
question below), bots arming or defusing C on **Copper Hills** (that
level's data center floor is missing from our export), bots crouching or
jumping, bots sprinting too much.

If the server fails to start bots, or the log mentions `DotRecast`, tell
me: the bots' map code now needs two new files in `Dependencies/`
(`DotRecast.Core.dll`, `DotRecast.Detour.dll`), and they are there.

## Before you start

- [ ] Every mode now starts with 1 player (`Matchmaking` → `MinPlayers`
  in `title.json`, set 2026-11-17). Bots join only Suppression and
  Sabotage, the modes with bot maps; elsewhere you'd be alone.
- [ ] Bots fill both teams to the mode's size (`FillToTeamSize` is on): 32
  a side in Suppression and Sabotage, less you.

## Suppression (any of the three maps)

The crash fix:
- [ ] The bots do **not** all vanish and come back together at any point.
  (Last time this was the brain crashing; if it happens, note the time.)

Shooting:
- [ ] Far away (past about 60 m) bots miss a lot more than before, and
  often don't shoot at all unless you shoot at them first.
- [ ] Far away they fire short bursts, not a long hold.
- [ ] Up close (under 20 m) they're as dangerous as before. Too dangerous?
  Say so; that's the next knob.

Seeing and moving:
- [ ] Bots don't spot you from across the map or from the side of their
  view as quickly as before; standing still makes you harder to notice.
- [ ] Some bots camp: they sit at one spot looking one way for a minute or
  two.
- [ ] After you kill a bot, sometimes it comes straight back for you;
  sometimes bots come toward gunfire; a few come round the side.
- [ ] Bots spend their time between the two sides, not wandering empty
  corners.

People and bots:
- [ ] Bots are already in the match while you're loading in.
- [ ] Someone joining takes a bot's place.

Still unknown, please look:
- [ ] When a bot is above or below you, does it aim **at** you (not the
  opposite way)?
- [ ] Can you see a bot's gun firing (muzzle flash, tracers)?

## Sabotage

Since 15:03 the three Sabotage maps are navigation meshes: bots walk
upstairs, indoors and drop off ledges. Bots can work A, B and C on **Syr
Darya Uplink** and **Darien Network**, and A and B on **Copper Hills**.
Syr Darya is the one to test on (its uplinks are upstairs).

Moving on the new maps:
- [ ] Bots go up stairs and along upper floors, and their feet stay on
  the floor: not floating above it or sunk into it, especially on stairs
  and slopes.
- [ ] Bots that run off a ledge or a spawn platform look like they drop,
  not jump or teleport.
- [ ] No bots stuck walking into walls or doorways for long.

Start of the match:
- [ ] The defenders get onto the map about 15 s before the attackers
  (that's MAG's own rule: defenders' first wave at 20 s, attackers' at 35 s).

Teams and roles:
- [ ] A few bots on each side ignore the objectives and just hunt.
- [ ] Most defending bots go to A and B, only a few wait at C.
- [ ] The attacking bots split between A and B, unevenly.

The uplinks:
- [ ] A bot reaches an uplink and the HUD shows it being hacked.
- [ ] Does the hacking bot look like it's doing anything? (No animation is
  sent; it may just stand there.)
- [ ] The uplink flips to the attackers after its 20 s hold.
- [ ] Defending bots run back to undo a hack while it counts down, or to
  retake a captured uplink.
- [ ] Try it yourself: hack an uplink as an attacker. Do defending bots
  come for you?

After both uplinks fall (A and B lock, C opens):
- [ ] Nobody keeps guarding A or B; both sides move to C.
- [ ] Anything odd when C opens?
- [ ] (Syr Darya, Darien Network) An attacking bot arms C; the HUD shows it
  armed. Defending bots come to defuse it. Can a bot's charge win the match?

One question only you can answer: in **SVER** Suppression, can players get
onto the open hills outside the walled compound? The new map of it only
covers inside the walls; the old one reached the hills. Until I know, SVER
stays on the old map.

## After the match

- [ ] In `medius-testserver/logs.txt` (or the newest `server.log.*`),
  search for `agent fault` and `link failed`. Either means a bug: send me
  the line.
- [ ] Tell me the recording's file name, and the times of anything that
  looked wrong. In the viewer, Sabotage recordings have a **Floors** button
  (all, upper, lower); the player panel says which floor someone is on.
