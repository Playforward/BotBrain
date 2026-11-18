using System.Numerics;
using BotBrain.Core;
using BotBrain.Maps;
using BotBrain.MapTool;
using BotBrain.Protocol;
using BotBrain.Sandbox;

namespace BotBrain.Tests;

/// <summary>The navigation mesh (NavMap, the map tool's NavMeshBuild): floors above floors, and the package that carries them.</summary>
public class NavMapTests
{
    /// <summary>
    /// A 40 m square of ground; over its middle, an upper floor 4 m up
    /// (a slab, 10 by 10 m, its underside 3.8 m up); a ramp from the ground
    /// to its east edge; and, off to the side, a roof with no way onto it.
    /// </summary>
    private static TriangleMesh TwoStoreys()
    {
        List<Vector3> v = [];
        List<int> t = [];
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int i = v.Count;
            v.AddRange([a, b, c, d]);
            t.AddRange([i, i + 1, i + 2, i, i + 2, i + 3]);
        }
        void Flat(float x0, float y0, float x1, float y1, float z) => Quad(new(x0, y0, z), new(x1, y0, z), new(x1, y1, z), new(x0, y1, z));
        Flat(0, 0, 40, 40, 0);
        Flat(15, 15, 25, 25, 4);         // the upper floor
        Flat(15, 15, 25, 25, 3.8f);      // its underside
        Quad(new(25, 16, 4), new(35, 16, 0), new(35, 19, 0), new(25, 19, 4)); // ramp, about 22 degrees
        Flat(2, 30, 8, 36, 6);           // a roof on nothing, out of reach
        Flat(2, 30, 8, 36, 5.8f);
        return new TriangleMesh([.. v], [.. t]);
    }

    [Fact]
    public void Both_floors_exist_over_the_same_spot_and_the_upper_one_is_reached_by_the_ramp()
    {
        NavMap nav = NavMeshBuild.Build(TwoStoreys(), NavMeshBuild.Default, out _);
        Assert.True(nav.Floor(new Vector3(20, 20, 0.5f), out _, out Vector3 ground, across: 0.5f, below: 1f, above: 0.5f));
        Assert.True(nav.Floor(new Vector3(20, 20, 4.5f), out _, out Vector3 upper, across: 0.5f, below: 1f, above: 0.5f));
        Assert.InRange(ground.Z, -0.3f, 0.3f);
        Assert.InRange(upper.Z, 3.7f, 4.3f);

        float walk = nav.WalkLength(new Vector3(5, 5, 0), upper);
        Assert.True(float.IsFinite(walk), "the upper floor can be walked to");
        Assert.True(walk > Vector2.Distance(new(5, 5), new(20, 20)) + 10f, $"by way of the ramp, not straight up: {walk:F1} m");

        int before = nav.Count().Polygons;
        int kept = nav.KeepReachable([new Vector3(5, 5, 0)]);
        Assert.True(kept < before, "the roof nobody can reach is switched off");
        Assert.True(float.IsPositiveInfinity(nav.WalkLength(new Vector3(5, 5, 0), new Vector3(5, 33, 6))));
        Assert.True(float.IsFinite(nav.WalkLength(new Vector3(5, 5, 0), upper)));
    }

    [Fact]
    public void A_package_with_a_navigation_mesh_is_version_4_with_point_heights_and_one_without_stays_version_2()
    {
        TriangleMesh mesh = TwoStoreys();
        GridMap grid = new("two-storeys", 4, 4, 10f, new byte[16], [], mesh: mesh);
        byte[] plain = grid.Serialize();
        Assert.Equal(2, BitConverter.ToInt32(plain, 4));

        NavMap nav = NavMeshBuild.Build(mesh, NavMeshBuild.Default, out _);
        nav.KeepReachable([new Vector3(5, 5, 0)]);
        GridMap withNav = grid.WithNav(nav, [new MapPoint(MapPointKind.Vantage, 255, 0, new Vector2(20, 20), 2f, 4f)]);
        byte[] bytes = withNav.Serialize();
        Assert.Equal(4, BitConverter.ToInt32(bytes, 4));
        Assert.NotEqual(grid.Hash(), withNav.Hash());

        GridMap read = GridMap.Deserialize(bytes);
        Assert.NotNull(read.Nav);
        Assert.Equal(4f, read.PointAt(Assert.Single(read.PointsOf(MapPointKind.Vantage))).Z); // an upstairs camp spot keeps its floor
        Assert.Equal(NavMeshBuild.Default, read.Nav!.BuiltFor);
        Assert.Equal(nav.WalkLength(new Vector3(5, 5, 0), new Vector3(20, 20, 4)), read.Nav.WalkLength(new Vector3(5, 5, 0), new Vector3(20, 20, 4)), 2);
        Assert.True(float.IsPositiveInfinity(read.Nav.WalkLength(new Vector3(5, 5, 0), new Vector3(5, 33, 6))), "switched-off floors stay off");
        Assert.Equal(grid.Hash(), GridMap.Deserialize(plain).Hash());
    }

    /// <summary>
    /// The brain on the mesh (M11 stage 2): a bot starting on the ground walks
    /// up the ramp to an uplink on the upper floor, right over where it
    /// started, and hacks it. On the grid that floor does not exist.
    /// </summary>
    [Fact]
    public void A_bot_climbs_to_an_objective_on_the_upper_floor_and_works_it()
    {
        TriangleMesh mesh = TwoStoreys();
        List<MapSeed> seeds = [new(new Vector3(5, 5, 0.5f), 0, 0), new(new Vector3(35, 35, 0.5f), 1, 1)];
        GridMap grid = MeshMapBuilder.Build("two-storeys", mesh, seeds, new MeshMapOptions { PlayRadius = 0, InterestPoints = 4, VantagePoints = 0 }, out _);
        NavMap nav = NavMeshBuild.Build(mesh, NavMeshBuild.Default, out _);
        nav.KeepReachable(seeds.Select(s => s.Position));
        GridMap map = grid.WithNav(nav);

        string file = Path.Combine(Path.GetTempPath(), $"bb-upstairs-{Environment.ProcessId}.json");
        File.WriteAllText(file, """
            { "objectives": [ { "id": 0, "name": "A", "x": 20, "y": 20, "z": 5, "radius": 2.5, "owner": 1, "work": 3, "hold": 5, "undo": 3 } ] }
            """);
        Simulation sim = new(map, 5) { Interacts = new InteractObjectives(file) };
        File.Delete(file);
        sim.AddPlayer(PlayerKind.Bot, 0);
        World world = new(1, sim.BuildManifest(), map, new BrainOptions { ForceFullFidelity = true });
        world.AddAgent(0, 0);
        world.SetObjectives(sim.BuildObjectives());
        ObserveFrame observe = new();
        ActFrame act = new();
        EventBatch events = new();
        float highest = float.NegativeInfinity;
        for (int t = 0; t < 60 * Simulation.TickHz && sim.Interacts!.Claims == 0; t++)
        {
            sim.Step();
            sim.FillObserve(observe);
            sim.FillEvents(events);
            world.OnEvents(events);
            world.Tick(observe, act);
            sim.ApplyAct(act);
            highest = MathF.Max(highest, sim.Players[0].Z);
        }
        Assert.True(highest > 3.5f, $"the bot never went upstairs (highest {highest:F1} m)");
        Assert.True(sim.Interacts!.Claims > 0, sim.Interacts.Summary());
        Assert.Equal(0, world.Stats.Faults);
    }
}
