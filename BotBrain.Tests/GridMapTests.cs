using System.Numerics;
using BotBrain.Maps;

namespace BotBrain.Tests;

public class GridMapTests
{
    /// <summary>A 40 m square with a wall down the middle and a gap near the far end.</summary>
    internal static GridMap Walled(bool gap = true, List<MapPoint>? points = null)
    {
        const int size = 40;
        byte[] blocked = new byte[size * size];
        for (int i = 0; i < size; i++)
            blocked[i] = blocked[(size - 1) * size + i] = blocked[i * size] = blocked[i * size + size - 1] = 1;
        for (int y = 1; y < (gap ? 31 : size - 1); y++)
            blocked[y * size + 20] = 1;
        return new GridMap("walled", size, size, 1f, blocked, points ?? []);
    }

    [Fact]
    public void A_wall_blocks_sight_and_a_gap_does_not()
    {
        GridMap map = Walled();
        Assert.False(map.LineOfSight(new Vector2(10, 10), new Vector2(30, 10)));
        Assert.True(map.LineOfSight(new Vector2(10, 35), new Vector2(30, 35)));
        Assert.True(map.LineOfSight(new Vector2(5, 5), new Vector2(15, 25)));
    }

    [Fact]
    public void A_ray_stops_at_the_wall()
    {
        GridMap map = Walled();
        float distance = map.Raycast(new Vector2(10.5f, 10.5f), new Vector2(1, 0), 100);
        Assert.InRange(distance, 9.4f, 9.6f);
        Assert.Equal(5f, map.Raycast(new Vector2(10.5f, 10.5f), new Vector2(1, 0), 5));
    }

    [Fact]
    public void A_path_goes_round_through_the_gap()
    {
        GridMap map = Walled();
        List<Vector2> path = [];
        Vector2 from = new(10.5f, 10.5f), to = new(30.5f, 10.5f);
        Assert.True(map.FindPath(from, to, path));
        Assert.True(Vector2.Distance(path[^1], to) < 1f);
        Vector2 previous = from;
        float length = 0;
        foreach (Vector2 point in path)
        {
            Assert.False(map.IsBlocked(point));
            Assert.True(map.ClearPath(previous, point), $"leg {previous} to {point} crosses a wall");
            length += Vector2.Distance(previous, point);
            previous = point;
        }
        Assert.True(length > 40f, "the path should detour through the gap, not cross the wall");
    }

    [Fact]
    public void No_path_through_a_solid_wall()
    {
        GridMap map = Walled(gap: false);
        Assert.False(map.FindPath(new Vector2(10.5f, 10.5f), new Vector2(30.5f, 10.5f), []));
    }

    [Fact]
    public void Following_a_flow_field_reaches_the_target_without_touching_walls()
    {
        GridMap map = Walled();
        Vector2 target = new(30.5f, 10.5f);
        FlowField field = map.BuildFlowField(target);
        Vector2 position = new(10.5f, 10.5f);
        Assert.InRange(field.DistanceFrom(position), 40f, 80f);
        int steps = 0;
        while (Vector2.Distance(position, target) > 1f && steps++ < 600)
        {
            Vector2 steer = field.SteerPoint(position);
            Vector2 to = steer - position;
            position += Vector2.Normalize(to) * MathF.Min(0.3f, to.Length());
            Assert.False(map.Collides(position.X, position.Y), $"walked into a wall at {position}");
        }
        Assert.True(Vector2.Distance(position, target) <= 1f, $"stopped at {position} after {steps} steps");
    }

    [Fact]
    public void A_map_package_survives_saving()
    {
        GridMap map = Walled(points: [new MapPoint(MapPointKind.Zone, 255, 2, new Vector2(5, 6), 12)]);
        GridMap copy = GridMap.Deserialize(map.Serialize());
        Assert.Equal(map.Hash(), copy.Hash());
        Assert.Equal(map.Points[0], copy.Points[0]);
        Assert.True(copy.IsBlockedCell(20, 10));
        Assert.False(copy.IsBlockedCell(20, 35));
    }
}
