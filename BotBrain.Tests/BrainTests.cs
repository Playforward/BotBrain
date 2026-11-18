using System.Net;
using System.Numerics;
using BotBrain.Core;
using BotBrain.Maps;
using BotBrain.Protocol;
using BotBrain.Sandbox;

namespace BotBrain.Tests;

public class BrainTests
{
    /// <summary>The brain and the sandbox in one loop, with no socket between them.</summary>
    private static World Run(Simulation sim, int ticks, BrainOptions? options = null, Action<Simulation>? eachTick = null)
    {
        World world = new(1, sim.BuildManifest(), sim.Map, options ?? new BrainOptions { ForceFullFidelity = true });
        foreach (SeatInfo seat in sim.BuildSeats().Slots)
            world.AddAgent(seat.Slot, seat.Team);
        world.SetObjectives(sim.BuildObjectives());
        ObserveFrame observe = new();
        ActFrame act = new();
        EventBatch events = new();
        for (int t = 0; t < ticks; t++)
        {
            sim.Step();
            sim.FillObserve(observe);
            sim.FillEvents(events);
            world.OnEvents(events);
            world.Tick(observe, act);
            sim.ApplyAct(act);
            eachTick?.Invoke(sim);
        }
        return world;
    }

    private static Simulation Match(ulong seed, int bots, int mapSize = 128)
    {
        Simulation sim = new(MapGenerator.Generate(seed, mapSize), seed);
        for (int i = 0; i < bots; i++)
            sim.AddPlayer(PlayerKind.Bot, (byte)(i % 2));
        return sim;
    }

    [Fact]
    public void A_bot_hits_a_target_in_view_but_not_instantly()
    {
        Simulation sim = new(MapGenerator.Generate(3, 64, buildings: false), 3);
        SimPlayer bot = sim.AddPlayer(PlayerKind.Bot, 0, new Vector2(20, 32), yaw: 0);
        SimPlayer target = sim.AddPlayer(PlayerKind.Dummy, 1, new Vector2(40, 36));
        uint firstShot = 0, firstHit = 0;
        Run(sim, 6 * Simulation.TickHz, eachTick: s =>
        {
            if (firstShot == 0 && bot.Shots > 0)
                firstShot = s.Tick;
            if (firstHit == 0 && bot.Hits > 0)
                firstHit = s.Tick;
        });

        Assert.True(firstHit > 0, "the bot never hit a standing target 20 m away");
        // Noticing, turning and squeezing the trigger each take time. Under a fifth of a second would be a machine.
        Assert.True(firstShot >= 6, $"first shot came after {firstShot} ticks");
        Assert.True(bot.MaxYawStep <= Simulation.Look.MaxTurnDegS * MathF.PI / 180f * Simulation.Dt + 1e-4f);
    }

    [Fact]
    public void A_bot_does_not_shoot_what_is_behind_a_wall()
    {
        List<MapPoint> points =
        [
            new(MapPointKind.Zone, 255, 0, new Vector2(10, 20), 6),
            new(MapPointKind.Spawn, 0, 0, new Vector2(5, 5), 2),
            new(MapPointKind.Spawn, 1, 0, new Vector2(35, 5), 2),
        ];
        Simulation sim = new(GridMapTests.Walled(gap: false, points), 4);
        SimPlayer bot = sim.AddPlayer(PlayerKind.Bot, 0, new Vector2(12, 20), yaw: 0);
        SimPlayer target = sim.AddPlayer(PlayerKind.Dummy, 1, new Vector2(28, 20));
        World world = Run(sim, 8 * Simulation.TickHz);

        Assert.Equal(0, bot.Shots);
        Assert.Equal(100f, target.Health);
        Assert.Equal(0, world.Stats.Noticed);
    }

    [Fact]
    public void The_same_seed_gives_the_same_match()
    {
        Simulation first = Match(7, 16), second = Match(7, 16), other = Match(8, 16);
        Run(first, 20 * Simulation.TickHz);
        Run(second, 20 * Simulation.TickHz);
        Run(other, 20 * Simulation.TickHz);
        Assert.Equal(first.Fingerprint(), second.Fingerprint());
        Assert.NotEqual(first.Fingerprint(), other.Fingerprint());
    }

    [Fact]
    public void Bots_move_fight_and_take_zones()
    {
        Simulation sim = Match(11, 32);
        World world = Run(sim, 90 * Simulation.TickHz);
        Assert.True(sim.Players.Sum(p => p.Shots) > 50, sim.Summary());
        Assert.True(sim.Players.Sum(p => p.Kills) > 0, sim.Summary());
        Assert.True(sim.Zones.Sum(z => z.Captures) > 0, sim.Summary());
        Assert.All(sim.Players, p => Assert.True(p.Distance > 20f, $"{p.Id} barely moved"));
        Assert.True(world.Stats.StuckEvents < 40, $"{world.Stats.StuckEvents} stuck recoveries");
    }

    /// <summary>
    /// Open ground 120 m wide with each side's spawn at one end, roam points on
    /// the way between them and as many again in a 40 m strip along the top
    /// where nobody has a reason to go.
    /// </summary>
    private static GridMap RoamingField()
    {
        const int size = 120;
        byte[] blocked = new byte[size * size];
        for (int i = 0; i < size; i++)
            blocked[i] = blocked[(size - 1) * size + i] = blocked[i * size] = blocked[i * size + size - 1] = 1;
        List<MapPoint> points = [];
        for (int i = 0; i < 4; i++)
        {
            points.Add(new MapPoint(MapPointKind.Spawn, 0, i, new Vector2(8, 30 + i * 2), 2));
            points.Add(new MapPoint(MapPointKind.Spawn, 1, 10 + i, new Vector2(112, 30 + i * 2), 2));
        }
        int id = 0;
        foreach (Vector2 p in (Vector2[])[new(35, 30), new(60, 30), new(85, 30), new(48, 18), new(72, 42), new(60, 12)])
            points.Add(new MapPoint(MapPointKind.Interest, 255, id++, p, 3));
        foreach (Vector2 p in (Vector2[])[new(10, 100), new(40, 110), new(60, 95), new(80, 110), new(110, 100), new(60, 112)])
            points.Add(new MapPoint(MapPointKind.Interest, 255, id++, p, 3));
        return new GridMap("roaming", size, size, 1f, blocked, points);
    }

    [Fact]
    public void Roaming_bots_head_between_the_sides_not_to_empty_corners()
    {
        // One side only, so nothing interrupts the roaming: this is the choice of where to go and nothing else.
        Simulation sim = new(RoamingField(), 21);
        for (int i = 0; i < 6; i++)
            sim.AddPlayer(PlayerKind.Bot, 0);
        long samples = 0, outOfTheWay = 0;
        Run(sim, 150 * Simulation.TickHz, eachTick: s =>
        {
            foreach (SimPlayer p in s.Players)
            {
                samples++;
                if (p.Position.Y > 80f)
                    outOfTheWay++;
            }
        });

        // Half the roam points are up there. Picked blindly, bots spend a good part of the match among them.
        Assert.True(outOfTheWay < samples * 0.08, $"{100.0 * outOfTheWay / samples:F1}% of the time in the empty strip");
    }

    [Fact]
    public void Bots_go_back_for_their_killer_toward_gunfire_and_round_the_side()
    {
        Simulation sim = new(RoamingField(), 21);
        for (int i = 0; i < 12; i++)
            sim.AddPlayer(PlayerKind.Bot, (byte)(i % 2));
        World world = Run(sim, 120 * Simulation.TickHz);

        Assert.True(sim.Players.Sum(p => p.Kills) > 5, sim.Summary());
        Assert.True(world.Stats.Hunts[(int)HuntReason.Revenge] > 0, "nobody went back for their killer");
        Assert.True(world.Stats.Hunts[(int)HuntReason.Gunfire] > 0, "nobody went toward gunfire");
        Assert.True(world.Stats.Flanks > 0, "nobody came round the side");
    }

    [Fact]
    public void Someone_standing_still_far_away_takes_seconds_to_spot_and_close_up_does_not()
    {
        // Open ground, nothing in the way, straight ahead of the bot.
        uint Spotted(float distance, int seconds)
        {
            Simulation sim = new(MapGenerator.Generate(3, 160, buildings: false), 3);
            sim.AddPlayer(PlayerKind.Bot, 0, new Vector2(20, 80), yaw: 0);
            sim.AddPlayer(PlayerKind.Dummy, 1, new Vector2(20 + distance, 80));
            uint at = 0;
            Run(sim, seconds * Simulation.TickHz, eachTick: s =>
            {
                if (at == 0 && s.Players[0].Shots > 0)
                    at = s.Tick;
            });
            return at;
        }
        uint near = Spotted(15, 3), far = Spotted(110, 3);
        Assert.True(near > 0 && near < 1.5f * Simulation.TickHz, $"a still target 15 m ahead was first shot at tick {near}");
        // A still figure 110 m off is a few pixels: no shot within the first second and a half.
        Assert.True(far == 0 || far > 1.5f * Simulation.TickHz, $"a still target 110 m ahead was shot at tick {far}");
    }

    /// <summary>
    /// A shooting range: one bot, one person standing still in the open, many
    /// different bots. Hit rate per shot, by how far the target was when the
    /// shot went out (the bot may walk closer first).
    /// </summary>
    internal static (int Shots, int Hits)[] Range(float[] bands, float[] starts, int people, bool underFire = false, int seconds = 8)
    {
        var table = new (int Shots, int Hits)[bands.Length - 1];
        foreach (float start in starts)
        {
            for (int n = 0; n < people; n++)
            {
                ulong seed = (ulong)(1000 + n);
                Simulation sim = new(MapGenerator.Generate(3, 160, buildings: false), seed);
                SimPlayer bot = sim.AddPlayer(PlayerKind.Bot, 0, new Vector2(10, 80), yaw: 0);
                SimPlayer target = sim.AddPlayer(PlayerKind.Dummy, 1, new Vector2(10 + start, 80));
                World world = new(1, sim.BuildManifest(), sim.Map, new BrainOptions { ForceFullFidelity = true, Seed = seed });
                foreach (SeatInfo seat in sim.BuildSeats().Slots)
                    world.AddAgent(seat.Slot, seat.Team);
                world.SetObjectives(sim.BuildObjectives());
                ObserveFrame observe = new();
                ActFrame act = new();
                EventBatch events = new();
                int shots = 0, hits = 0;
                for (int t = 0; t < seconds * Simulation.TickHz; t++)
                {
                    sim.Step();
                    // The target shoots at the bot now and then (a graze): the bot knows where from and answers at any range.
                    if (underFire && t % 45 == 15)
                    {
                        Vector2 toward = Vector2.Normalize(target.Position - bot.Position);
                        sim.Events.Add(new EventRecord { Kind = EventKind.Shot, A = target.Id, X = target.Position.X, Y = target.Position.Y, Z = target.Z + Simulation.Eye });
                        sim.Events.Add(new EventRecord { Kind = EventKind.Damage, A = bot.Id, B = target.Id, X = toward.X, Y = toward.Y, Value = 1 });
                    }
                    sim.FillObserve(observe);
                    sim.FillEvents(events);
                    world.OnEvents(events);
                    world.Tick(observe, act);
                    sim.ApplyAct(act);
                    if (bot.Shots == shots)
                        continue;
                    float d = Vector2.Distance(bot.Position, target.Position);
                    int band = Array.FindIndex(bands, b => d < b) - 1;
                    if (band >= 0)
                    {
                        table[band].Shots += bot.Shots - shots;
                        table[band].Hits += bot.Hits - hits;
                    }
                    shots = bot.Shots;
                    hits = bot.Hits;
                }
            }
        }
        return table;
    }

    private static string RangeText(float[] bands, (int Shots, int Hits)[] table) =>
        string.Join("  ", table.Select((t, i) => $"{bands[i]}-{bands[i + 1]} m {t.Hits}/{t.Shots} = {100.0 * t.Hits / Math.Max(1, t.Shots):F0}%"));

    [Fact]
    public void Bots_hit_like_people_close_up_and_far_away()
    {
        float[] bands = [0, 15, 35, 60, 120];
        var calm = Range(bands, [10, 25, 50, 85], people: 30);
        var shotAt = Range(bands, [85, 110], people: 30, underFire: true, seconds: 20);
        string text = RangeText(bands, calm) + " | under fire: " + RangeText(bands, shotAt);
        static float Rate((int Shots, int Hits) t) => t.Hits / (float)Math.Max(1, t.Shots);
        // Live 2026-11-16 the old aim hit 15-30% of shots past 50 m and one bot landed 5 of 9 at 85 m.
        // Still able to fight up close, but far away most of a burst misses.
        Assert.True(calm[0].Shots >= 50 && Rate(calm[0]) is > 0.25f and < 0.7f, text);
        Assert.True(shotAt[3].Shots >= 30 && Rate(shotAt[3]) is > 0.03f and < 0.25f, text);
        // Without being shot at, almost nobody opens up at a figure that far out.
        Assert.True(calm[3].Shots < 10, text);
    }

    [Fact]
    public void Some_bots_camp_at_one_spot_and_watch()
    {
        Simulation sim = new(RoamingField(), 21);
        for (int i = 0; i < 10; i++)
            sim.AddPlayer(PlayerKind.Bot, 0);
        Dictionary<uint, (Vector2 At, uint Since)> still = [];
        float longest = 0;
        World world = Run(sim, 150 * Simulation.TickHz, eachTick: s =>
        {
            foreach (SimPlayer p in s.Players)
            {
                if (!still.TryGetValue(p.Id, out var held) || Vector2.DistanceSquared(held.At, p.Position) > 1f)
                    still[p.Id] = (p.Position, s.Tick);
                else
                    longest = MathF.Max(longest, (s.Tick - held.Since) / (float)Simulation.TickHz);
            }
        });

        Assert.True(world.Stats.Camps > 0, "nobody camped");
        Assert.True(longest >= 20f, $"the longest anyone stayed in one place was {longest:F0} s");
    }

    [Fact]
    public void A_camp_that_ends_leaves_no_camp_goal_and_a_bad_agent_does_not_end_the_match()
    {
        // Live 2026-11-16 (63 bots): a camp ended on the way there, Decide
        // returned early because the tick's path searches were used up, the
        // agent kept Goal.Camp with no spot, and the camp skill indexed spot -1.
        // The exception ended the link and the body took all 63 bots out.
        Simulation sim = new(RoamingField(), 21);
        for (int i = 0; i < 10; i++)
            sim.AddPlayer(PlayerKind.Bot, 0);
        World world = new(1, sim.BuildManifest(), sim.Map, new BrainOptions { ForceFullFidelity = true });
        foreach (SeatInfo seat in sim.BuildSeats().Slots)
            world.AddAgent(seat.Slot, seat.Team);
        world.SetObjectives(sim.BuildObjectives());
        ObserveFrame observe = new();
        ActFrame act = new();
        EventBatch events = new();
        void Step()
        {
            sim.Step();
            sim.FillObserve(observe);
            sim.FillEvents(events);
            world.OnEvents(events);
            world.Tick(observe, act);
            sim.ApplyAct(act);
        }
        Agent? camper = null;
        for (int t = 0; t < 150 * Simulation.TickHz && camper is null; t++)
        {
            Step();
            camper = world.Agents.FirstOrDefault(a => a.Goal == Goal.Camp && a.Alive);
        }
        Assert.NotNull(camper);

        world.EndCamp(camper);
        Assert.NotEqual(Goal.Camp, camper.Goal);

        // The old bad state, forced: the camp skill must cope, not throw.
        camper.Goal = Goal.Camp;
        Step();
        Assert.NotEqual(Goal.Camp, camper.Goal);

        // Any other agent bug: the world counts it, resets the agent and goes on.
        Agent other = world.Agents.First(a => a != camper && a.Alive);
        other.Goal = Goal.Engage;
        other.TargetTrack = 99;
        other.TargetEntity = 12345;
        Step();
        Assert.Equal(1, world.Stats.Faults);
        Assert.Contains("IndexOutOfRange", world.LastFault);
        for (int t = 0; t < 5 * Simulation.TickHz; t++)
            Step();
        Assert.Equal(10, world.AgentCount);
        Assert.Equal(1, world.Stats.Faults);
    }

    /// <summary>
    /// Sabotage in the open field: team 1 owns two uplinks and a data center
    /// that needs both; team 0 attacks. Each team has a few seekers and an
    /// uneven split of defenders and attackers, the bigger part on its own
    /// side of the match; the attackers get an uplink, by holding interact at it.
    /// </summary>
    [Fact]
    public void Bots_take_roles_and_play_a_sabotage_match()
    {
        string file = Path.Combine(Path.GetTempPath(), $"bb-sabotage-{Environment.ProcessId}.json");
        File.WriteAllText(file, """
            { "objectives": [
              { "id": 0, "name": "A", "x": 36, "y": 22, "z": 1, "owner": 1, "work": 3, "hold": 20, "undo": 3 },
              { "id": 1, "name": "B", "x": 36, "y": 40, "z": 1, "owner": 1, "work": 3, "hold": 20, "undo": 3 },
              { "id": 2, "name": "C", "x": 100, "y": 31, "z": 1, "owner": 1, "requires": [0, 1], "work": 3.5, "hold": 45, "undo": 2.5, "retake": false, "final": true, "locks_requires": true } ] }
            """);
        Simulation sim = new(RoamingField(), 31) { Interacts = new InteractObjectives(file) };
        File.Delete(file);
        for (int i = 0; i < 32; i++)
            sim.AddPlayer(PlayerKind.Bot, (byte)(i % 2));
        World world = Run(sim, 120 * Simulation.TickHz);

        int Count(int team, Role role) => world.Agents.Count(a => a.Team == team && a.Role == role);
        Assert.InRange(Count(0, Role.Seeker) + Count(1, Role.Seeker), 1, 6);
        Assert.True(Count(1, Role.Defender) > Count(1, Role.Attacker), $"team 1 (owns everything): {Count(1, Role.Defender)} defenders, {Count(1, Role.Attacker)} attackers");
        Assert.True(Count(0, Role.Attacker) > Count(0, Role.Defender), $"team 0 (owns nothing): {Count(0, Role.Attacker)} attackers, {Count(0, Role.Defender)} defenders");
        foreach (int team in (int[])[0, 1])
        {
            int players = Count(team, Role.Defender) + Count(team, Role.Attacker);
            int major = Math.Max(Count(team, Role.Defender), Count(team, Role.Attacker));
            Assert.InRange(major, (int)MathF.Floor(players * 0.51f), (int)MathF.Ceiling(players * 0.57f) + 1);
        }
        Assert.True(sim.Interacts!.Claims > 0, sim.Interacts.Summary() + "\n" + world.Stats.Report(world.AgentCount, Simulation.TickHz));
        Assert.True(world.Stats.Interacts > 0);
        // The open field keeps everyone in sight of someone, so fighting takes much of the time here.
        Assert.True(world.Stats.GoalTicks[(int)Goal.Objective] > world.Stats.GoalTicks.Sum() / 10, "a real share of the time goes to the objectives");
        Assert.Equal(0, world.Stats.Faults);
        Assert.True(world.Stats.StuckEvents < 60, $"{world.Stats.StuckEvents} stuck recoveries");
    }

    [Fact]
    public void A_bot_the_body_takes_back_is_no_longer_driven()
    {
        Simulation sim = Match(13, 8);
        World world = new(1, sim.BuildManifest(), sim.Map, new BrainOptions { ForceFullFidelity = true });
        foreach (SeatInfo seat in sim.BuildSeats().Slots)
            world.AddAgent(seat.Slot, seat.Team);
        world.SetObjectives(sim.BuildObjectives());
        ObserveFrame observe = new();
        ActFrame act = new();
        EventBatch events = new();
        bool sentAfter = false;
        for (int t = 0; t < 10 * Simulation.TickHz; t++)
        {
            if (t == 5 * Simulation.TickHz)
                world.RemoveAgent(3);
            sim.Step();
            sim.FillObserve(observe);
            sim.FillEvents(events);
            world.OnEvents(events);
            world.Tick(observe, act);
            sim.ApplyAct(act);
            if (t > 5 * Simulation.TickHz)
                for (int i = 0; i < act.Count; i++)
                    sentAfter |= act.Records[i].Slot == 3;
        }
        Assert.Equal(7, world.AgentCount);
        Assert.False(sentAfter, "the brain kept sending controls for a bot it was told had left");
        Assert.Equal(1, world.Stats.AgentsLeft);
    }

    [Fact]
    public void Bots_nobody_can_see_run_at_reduced_fidelity()
    {
        Simulation unseen = Match(5, 16);
        World world = Run(unseen, 10 * Simulation.TickHz, new BrainOptions());
        Assert.True(world.Stats.ReducedAgentTicks > world.Stats.FullAgentTicks);

        Simulation watched = Match(5, 16, mapSize: 64);
        watched.AddPlayer(PlayerKind.Human, 0);
        World seen = Run(watched, 10 * Simulation.TickHz, new BrainOptions());
        Assert.True(seen.Stats.FullAgentTicks > 0);
    }

    [Fact]
    public void A_match_runs_over_a_socket()
    {
        Simulation sim = Match(9, 12);
        MemoryMapStore store = new();
        store.Add(sim.Map);
        using BrainServer server = new(store, new BrainOptions { ForceFullFidelity = true });
        World? closed = null;
        using ManualResetEventSlim done = new(false);
        server.WorldClosed += world =>
        {
            closed = world;
            done.Set();
        };
        int port = server.Start(IPAddress.Loopback, 0);

        RunResult result = new BodyLink("127.0.0.1", port).Run(sim, 5 * Simulation.TickHz, lockstep: true);

        Assert.True(done.Wait(5000, TestContext.Current.CancellationToken));
        Assert.Equal(150, result.Ticks);
        Assert.Equal(12, result.SeatsTaken);
        Assert.True(result.Welcome!.MapLoaded);
        Assert.Equal(150, closed!.Stats.Ticks);
        Assert.All(sim.Players, p => Assert.False(string.IsNullOrEmpty(p.Name)));
        Assert.True(sim.Players.Sum(p => p.Distance) > 100f);
    }

    [Fact]
    public void A_body_with_an_unknown_map_is_welcomed_but_not_driven()
    {
        Simulation sim = Match(9, 4);
        using BrainServer server = new(new MemoryMapStore(), new BrainOptions());
        int port = server.Start(IPAddress.Loopback, 0);
        RunResult result = new BodyLink("127.0.0.1", port).Run(sim, 30, lockstep: true);
        Assert.False(result.Welcome!.MapLoaded);
        Assert.Equal(0, result.SeatsTaken);
        Assert.Equal(30, result.Ticks);
        Assert.Equal(0f, sim.Players.Sum(p => p.Distance));
    }
}
