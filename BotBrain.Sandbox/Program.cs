using System.Net;
using BotBrain.Core;
using BotBrain.Maps;

namespace BotBrain.Sandbox;

/// <summary>
/// Runs a match in the sandbox world with every bot driven by the brain, and
/// reports what the brain cost.
///   --agents N       bots (default 255)
///   --humans N       stand-in humans who wander and count as observers (default 1)
///   --seconds N      match length (default 60)
///   --map N          map edge in metres (default 512)
///   --seed N         seed for the map, the match and the personas (default 1)
///   --all-full       every bot at full fidelity (the worst case)
///   --realtime       run at 30 Hz by the clock instead of as fast as possible
///   --brain H:P      use a brain that is already running, not one in this process
///   --export-map DIR write the map package where an external brain can find it
///   --map-file PATH  play on a map package built from a real level, not a generated town
///   --record DIR     record the match for the replay viewer (brain in this process only)
///   --humans-sit     stand-in humans stay where they spawn (someone in the match not playing)
///   --objectives F   objectives worked by hand (InteractObjectives.cs), e.g. a Sabotage level's
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        int agents = 255, humans = 1, seconds = 60, mapSize = 512;
        ulong seed = 1;
        bool allFull = false, realtime = false, humansSit = false;
        string? brain = null, exportMap = null, mapFile = null, record = null, objectives = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--agents": agents = int.Parse(args[++i]); break;
                case "--humans": humans = int.Parse(args[++i]); break;
                case "--seconds": seconds = int.Parse(args[++i]); break;
                case "--map": mapSize = int.Parse(args[++i]); break;
                case "--seed": seed = ulong.Parse(args[++i]); break;
                case "--all-full": allFull = true; break;
                case "--realtime": realtime = true; break;
                case "--brain": brain = args[++i]; break;
                case "--export-map": exportMap = args[++i]; break;
                case "--map-file": mapFile = args[++i]; break;
                case "--record": record = args[++i]; break;
                case "--objectives": objectives = args[++i]; break;
                case "--humans-sit": humansSit = true; break;
                default:
                    Console.Error.WriteLine($"unknown option {args[i]}");
                    return 2;
            }
        }

        GridMap map = mapFile is null ? MapGenerator.Generate(seed, mapSize) : GridMap.Deserialize(File.ReadAllBytes(mapFile));
        Simulation sim = new(map, seed) { HumansSit = humansSit };
        if (objectives is not null)
        {
            sim.Interacts = new InteractObjectives(objectives);
            sim.Waves = sim.Interacts.Waves;
        }
        for (int i = 0; i < humans; i++)
            sim.AddPlayer(PlayerKind.Human, (byte)(i % 2));
        for (int i = 0; i < agents; i++)
            sim.AddPlayer(PlayerKind.Bot, (byte)((i + humans) % 2));
        if (exportMap is not null)
            Console.WriteLine($"map package written to {new DirectoryMapStore(exportMap).Save(map)}");

        string host = "127.0.0.1";
        int port;
        BrainServer? server = null;
        World? closed = null;
        using ManualResetEventSlim done = new(false);
        if (brain is null)
        {
            MemoryMapStore store = new();
            store.Add(map);
            server = new BrainServer(store, new BrainOptions { ForceFullFidelity = allFull, Seed = seed, RecordDirectory = record });
            server.Log += Console.WriteLine;
            server.WorldClosed += world =>
            {
                closed = world;
                done.Set();
            };
            port = server.Start(IPAddress.Loopback, 0);
        }
        else
        {
            string[] parts = brain.Split(':');
            host = parts[0];
            port = int.Parse(parts[1]);
        }

        Console.WriteLine($"sandbox: {agents} bots, {humans} stand-in humans, {(mapFile is null ? $"{mapSize} m map" : map.Id)}, {seconds} s, "
            + (realtime ? "real time" : "lockstep") + (allFull ? ", all at full fidelity" : ""));
        RunResult result = new BodyLink(host, port).Run(sim, seconds * Simulation.TickHz, lockstep: !realtime);
        if (result.Welcome is { } welcome)
        {
            if (!welcome.MapLoaded)
                Console.WriteLine("the brain has no package for this map, so it drove nothing");
            if (welcome.Rejected.Count > 0)
                Console.WriteLine("not supported by this brain: " + string.Join(", ", welcome.Rejected));
        }

        Console.WriteLine();
        Console.WriteLine(sim.Summary());
        Console.WriteLine($"  body       {result.SimMsPerTick:F3} ms per tick for the sandbox's own simulation; {result.Ticks} ticks in {result.WallSeconds:F1} s of wall time");
        if (realtime)
            Console.WriteLine($"  lateness   {result.LateTicks} of {result.Ticks} ticks had a living bot whose controls were more than 4 ticks old"
                + (result.LateTicks > 0 ? $" (first at tick {result.FirstLateTick}, last at tick {result.LastLateTick})" : ""));
        if (server is not null)
        {
            done.Wait(5000);
            Console.WriteLine();
            Console.WriteLine(closed is null ? "the brain did not report" : closed.Stats.Report(closed.AgentCount, Simulation.TickHz));
            server.Dispose();
        }
        return 0;
    }
}
