# BotBrain

One brain that plays as many bot players, in any game that attaches a body
to it.

| To do this | Read |
|---|---|
| Pick this project up: the owner's rules, where everything is, the commands, the work queue | [AGENTS.md](AGENTS.md) |
| Understand the design and the wire format; see what is built and what it costs | [docs/ATTACH-PROTOCOL.md](docs/ATTACH-PROTOCOL.md) (section 14 for what is built) |
| Change how bots behave | [docs/CHANGING-BEHAVIOUR.md](docs/CHANGING-BEHAVIOUR.md) |
| Give bots a new action, objective type or vehicles | [docs/ADDING-CAPABILITIES.md](docs/ADDING-CAPABILITIES.md) |
| Add another game | [docs/ADDING-A-GAME.md](docs/ADDING-A-GAME.md) |

| Project | What it is |
|---|---|
| `BotBrain.Protocol` | Message framing and the per-tick records. |
| `BotBrain.Maps` | The map package: line of sight, rays, routes. A grid with heights, plus the level's triangles for sight. |
| `BotBrain.Core` | The brain: perception, decisions, skills, the motor model. |
| `BotBrain.Host` | The brain as a standalone process that bodies connect to. |
| `BotBrain.Sandbox` | A small simulated shooter that acts as a body, for development and measurement. |
| `BotBrain.MapTool` | Builds a map package from a level's triangles and spawn points. |
| `viewer/index.html` | Replays a recorded match from above, in a browser. |
| `BotBrain.Tests` | Tests for all of the above. |

## Run

```sh
dotnet test BotBrain.Tests -c Release
dotnet run --project BotBrain.Sandbox -c Release -- --agents 255 --humans 1 --seconds 600 --all-full
```

The second command runs a 256-player match with the brain in the same
process and prints what the match looked like and what the brain cost.
`--help` is not implemented; the options are listed at the top of
`BotBrain.Sandbox/Program.cs`.

To run the brain as its own process:

```sh
dotnet run --project BotBrain.Host -c Release -- --port 24800 --maps maps
dotnet run --project BotBrain.Sandbox -c Release -- --brain 127.0.0.1:24800 --export-map maps --realtime
```

## Record a match and replay it

The brain can write each match to a file: everything the body reported
about every player, and for each bot what it believed, what it was trying
to do and what its thumbs did. Give it a folder:

```sh
dotnet run --project BotBrain.Sandbox -c Release -- --agents 31 --seconds 180 --record recordings
dotnet run --project BotBrain.Host -c Release -- --record recordings
```

(`BrainOptions.RecordDirectory` for a process that hosts the brain itself.)
One `.bbrec` file is written per match. A match still running, or one whose
server was stopped, leaves a file that ends early; the viewer plays what is
there.

Open `viewer/index.html` in a browser and drop a `.bbrec` on it. It needs
nothing installed and sends nothing anywhere. Click a player to see their
trail, and for a bot its destination, the enemies it knows about, its stick
positions and its goal over time. The charts show turn rate and speed
around the current moment for bots and people alike, and the histogram
compares how fast the selected player turns with the people in the match.

People appear as "Player" and a number: the protocol does not carry their
names yet.

## Hosting the brain elsewhere

Route search and the ray loops are marked for full optimisation on first
use, so a host needs no special settings. (`Directory.Build.props` also
sets `TieredCompilationQuickJitForLoops=false` for the programs here.)
