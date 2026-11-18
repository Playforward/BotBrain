using System.Numerics;
using BotBrain.Maps;

namespace BotBrain.Tests;

public class MeshMapTests
{
    private static void Quad(List<Vector3> v, List<int> t, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        int first = v.Count;
        v.AddRange([a, b, c, d]);
        t.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
    }

    /// <summary>
    /// Ground 40 m square at height 0, a 10 m platform 3 m up in one corner,
    /// and a thin wall 2 m high across the ground at x = 30 with a 2 m gap.
    /// </summary>
    private static TriangleMesh Level()
    {
        List<Vector3> v = [];
        List<int> t = [];
        Quad(v, t, new(0, 0, 0), new(40, 0, 0), new(40, 40, 0), new(0, 40, 0));
        Quad(v, t, new(0, 0, 3), new(10, 0, 3), new(10, 10, 3), new(0, 10, 3));
        Quad(v, t, new(30.02f, 0, 0), new(30.02f, 30, 0), new(30.02f, 30, 2), new(30.02f, 0, 2));
        Quad(v, t, new(30.02f, 32, 0), new(30.02f, 40, 0), new(30.02f, 40, 2), new(30.02f, 32, 2));
        return new TriangleMesh([.. v], [.. t]);
    }

    private static GridMap Build(out MeshMapReport report) =>
        MeshMapBuilder.Build("test", Level(), [new MapSeed(new Vector3(5, 5, 3.4f), 0, 0)], new MeshMapOptions { PlayRadius = 0 }, out report);

    [Fact]
    public void Interest_points_lie_on_the_ways_between_the_sides_not_the_empty_edges()
    {
        // Open ground 200 x 200 m, one side's spawn at each end of a line across the middle.
        List<Vector3> v = [];
        List<int> t = [];
        Quad(v, t, new(0, 0, 0), new(200, 0, 0), new(200, 200, 0), new(0, 200, 0));
        MapSeed[] seeds = [new(new Vector3(20, 100, 0.4f), 0, 0), new(new Vector3(180, 100, 0.4f), 1, 1)];
        MeshMapOptions options = new() { CellSize = 1f, PlayRadius = 0 };
        GridMap map = MeshMapBuilder.Build("open", new TriangleMesh([.. v], [.. t]), seeds, options, out MeshMapReport report);

        Assert.True(report.InterestOnRoutes);
        List<MapPoint> interest = [.. map.PointsOf(MapPointKind.Interest)];
        Assert.Equal(options.InterestPoints, interest.Count);
        Vector2 home = new(20, 100), away = new(180, 100);
        foreach (MapPoint p in interest)
        {
            // Straight-line distances are never longer than walking ones, so this holds whenever the walk does.
            float sum = Vector2.Distance(p.Position, home) + Vector2.Distance(p.Position, away);
            Assert.True(sum <= 160f * (1 + options.InterestRouteSlack) + 2f, $"{p.Position} is {sum - 160f:F0} m out of the way");
            Assert.True(Vector2.Distance(p.Position, home) >= options.InterestSpawnClearance - 1f);
            Assert.True(Vector2.Distance(p.Position, away) >= options.InterestSpawnClearance - 1f);
        }
        // Still spread out across the band, not bunched on the line.
        Assert.Contains(interest, p => p.Position.Y > 115f);
        Assert.Contains(interest, p => p.Position.Y < 85f);

        // Vantage points: found, on the routes too, and not on top of each other.
        List<MapPoint> vantage = [.. map.PointsOf(MapPointKind.Vantage)];
        Assert.Equal(options.VantagePoints, vantage.Count);
        foreach (MapPoint p in vantage)
        {
            Assert.True(Vector2.Distance(p.Position, home) + Vector2.Distance(p.Position, away) <= 160f * (1 + options.InterestRouteSlack) + 2f);
            Assert.All(vantage, q => Assert.True(q.Id == p.Id || Vector2.Distance(p.Position, q.Position) >= 25f));
        }
    }

    [Fact]
    public void A_ray_stops_at_a_wall_and_passes_over_it()
    {
        TriangleMesh mesh = Level();
        Assert.Equal(10.02f, mesh.Raycast(new Vector3(20, 10, 1), new Vector3(1, 0, 0), 100), 2);
        Assert.Equal(100f, mesh.Raycast(new Vector3(20, 10, 2.5f), new Vector3(1, 0, 0), 100));
        Assert.False(mesh.LineOfSight(new Vector3(20, 10, 1.6f), new Vector3(35, 10, 1.6f)));
        Assert.True(mesh.LineOfSight(new Vector3(20, 31, 1.6f), new Vector3(35, 31, 1.6f)));   // through the gap
        Assert.Equal(1f, mesh.Raycast(new Vector3(20, 20, 1), new Vector3(0, 0, -1), 50), 3);   // straight down to the ground
    }

    [Fact]
    public void The_walkable_area_is_found_from_a_seed_on_a_platform()
    {
        GridMap map = Build(out MeshMapReport report);
        Assert.Equal(1, report.SeedsOnGround);
        Assert.True(report.Drops > 0);
        Assert.Equal(3f, map.GroundAt(new Vector2(5, 5)), 2);
        Assert.Equal(0f, map.GroundAt(new Vector2(20, 20)), 2);
        Assert.Equal(0f, map.GroundAt(new Vector2(35, 10)), 2);   // beyond the wall, reached through the gap
        Assert.False(map.IsBlocked(new Vector2(35, 10)));
    }

    [Fact]
    public void A_drop_is_one_way()
    {
        GridMap map = Build(out _);
        // Off the platform and across the ground: fine. Back up: no.
        Assert.False(float.IsInfinity(map.BuildFlowField(new Vector2(20, 20)).DistanceFrom(new Vector2(5, 5))));
        Assert.True(float.IsInfinity(map.BuildFlowField(new Vector2(5, 5)).DistanceFrom(new Vector2(20, 20))));

        // A body walking off the edge keeps going; one walking at it from below is stopped.
        Vector2 top = new(9.2f, 5.25f);
        for (int step = 0; step < 20; step++)
            top = map.Move(top, top + new Vector2(0.1f, 0));
        Assert.True(top.X > 10.5f, $"stopped at {top}");
        Vector2 below = new(11.2f, 5.25f);
        for (int step = 0; step < 20; step++)
            below = map.Move(below, below - new Vector2(0.1f, 0));
        Assert.True(below.X >= 10f, $"walked up the wall to {below}");
    }

    [Fact]
    public void A_wall_thinner_than_a_cell_still_stops_a_body()
    {
        GridMap map = Build(out _);
        Vector2 at = new(28.25f, 10.25f);
        for (int step = 0; step < 40; step++)
            at = map.Move(at, at + new Vector2(0.1f, 0));
        Assert.True(at.X < 30.1f, $"walked through the wall to {at}");
        Assert.False(map.ClearPath(new Vector2(28.25f, 10.25f), new Vector2(32.25f, 10.25f)));
        Assert.True(map.ClearPath(new Vector2(28.25f, 31.25f), new Vector2(32.25f, 31.25f)));

        // The route to the other side goes round through the gap.
        List<Vector2> path = [];
        Assert.True(map.FindPath(new Vector2(28.25f, 10.25f), new Vector2(32.25f, 10.25f), path));
        Assert.Contains(path, p => p.Y > 30f);
    }

    [Fact]
    public void A_built_map_survives_saving_with_its_mesh()
    {
        GridMap map = Build(out _);
        GridMap copy = GridMap.Deserialize(map.Serialize());
        Assert.Equal(map.Hash(), copy.Hash());
        Assert.True(copy.HasGround);
        Assert.Equal(map.Mesh!.TriangleCount, copy.Mesh!.TriangleCount);
        Assert.Equal(map.GroundAt(new Vector2(5, 5)), copy.GroundAt(new Vector2(5, 5)));
        Assert.Equal(map.Origin, copy.Origin);
        Assert.False(copy.LineOfSight(new Vector3(20, 10, 1.6f), new Vector3(35, 10, 1.6f)));
    }
}
