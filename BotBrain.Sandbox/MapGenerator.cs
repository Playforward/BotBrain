using System.Numerics;
using BotBrain.Core;
using BotBrain.Maps;

namespace BotBrain.Sandbox;

/// <summary>
/// Makes a square town: blocks and walls scattered over open ground, two
/// spawn strips on opposite edges and three zones down the middle.
/// </summary>
internal static class MapGenerator
{
    public const float ZoneRadius = 12f;

    public static GridMap Generate(ulong seed, int size, bool buildings = true)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            GridMap? map = TryGenerate(seed + (ulong)attempt * 7919UL, size, buildings);
            if (map is not null)
                return map;
        }
        throw new InvalidOperationException("could not generate a connected map");
    }

    private static GridMap? TryGenerate(ulong seed, int size, bool buildings)
    {
        Rng rng = new(seed);
        byte[] blocked = new byte[size * size];
        for (int i = 0; i < size; i++)
            blocked[i] = blocked[(size - 1) * size + i] = blocked[i * size] = blocked[i * size + size - 1] = 1;

        List<MapPoint> points = [];
        float[] zoneY = [0.22f, 0.5f, 0.78f];
        for (int z = 0; z < zoneY.Length; z++)
            points.Add(new MapPoint(MapPointKind.Zone, 255, z, new Vector2(size * 0.5f, size * zoneY[z]), ZoneRadius));
        int spawns = Math.Max(8, size / 16);
        for (int team = 0; team < 2; team++)
        {
            for (int s = 0; s < spawns; s++)
            {
                float x = size * (team == 0 ? rng.Range(0.03f, 0.09f) : rng.Range(0.91f, 0.97f));
                float y = size * (0.12f + 0.76f * (s + 0.5f) / spawns);
                points.Add(new MapPoint(MapPointKind.Spawn, (byte)team, s, new Vector2(x, y), 2f));
            }
        }

        if (buildings)
        {
            int blocks = size * size / 700;
            for (int b = 0; b < blocks; b++)
            {
                int w = 5 + rng.NextInt(18), h = 5 + rng.NextInt(18);
                Fill(blocked, size, points, 2 + rng.NextInt(size - w - 4), 2 + rng.NextInt(size - h - 4), w, h);
            }
            int walls = size * size / 2500;
            for (int b = 0; b < walls; b++)
            {
                int length = 8 + rng.NextInt(23);
                bool horizontal = rng.NextInt(2) == 0;
                int w = horizontal ? length : 1, h = horizontal ? 1 : length;
                Fill(blocked, size, points, 2 + rng.NextInt(size - w - 4), 2 + rng.NextInt(size - h - 4), w, h);
            }
        }

        // Anything walled off from the first spawn becomes solid, so every free cell is reachable.
        Vector2 origin = points.First(p => p.Kind == MapPointKind.Spawn).Position;
        bool[] reached = new bool[size * size];
        Stack<int> stack = new();
        int start = (int)origin.Y * size + (int)origin.X;
        if (blocked[start] != 0)
            return null;
        reached[start] = true;
        stack.Push(start);
        while (stack.Count > 0)
        {
            int cell = stack.Pop();
            int cx = cell % size, cy = cell / size;
            for (int d = 0; d < 4; d++)
            {
                int nx = cx + (d == 0 ? 1 : d == 1 ? -1 : 0), ny = cy + (d == 2 ? 1 : d == 3 ? -1 : 0);
                int n = ny * size + nx;
                if (nx < 0 || ny < 0 || nx >= size || ny >= size || blocked[n] != 0 || reached[n])
                    continue;
                reached[n] = true;
                stack.Push(n);
            }
        }
        for (int i = 0; i < blocked.Length; i++)
        {
            if (!reached[i])
                blocked[i] = 1;
        }
        foreach (MapPoint p in points)
        {
            if (blocked[(int)p.Position.Y * size + (int)p.Position.X] != 0)
                return null;
        }

        for (int i = 0, tries = 0; i < 24 && tries < 10000; tries++)
        {
            int x = 2 + rng.NextInt(size - 4), y = 2 + rng.NextInt(size - 4);
            if (blocked[y * size + x] != 0)
                continue;
            points.Add(new MapPoint(MapPointKind.Interest, 255, i++, new Vector2(x + 0.5f, y + 0.5f), 3f));
        }
        return new GridMap($"sandbox-{size}-{seed}", size, size, 1f, blocked, points);
    }

    /// <summary>Blocks a rectangle unless it would cover a zone or a spawn strip.</summary>
    private static void Fill(byte[] blocked, int size, List<MapPoint> points, int x, int y, int w, int h)
    {
        if (x < size * 0.11f || x + w > size * 0.89f)
            return;
        foreach (MapPoint p in points)
        {
            float keep = p.Radius + 4f;
            if (p.Position.X > x - keep && p.Position.X < x + w + keep && p.Position.Y > y - keep && p.Position.Y < y + h + keep)
                return;
        }
        for (int cy = y; cy < y + h; cy++)
        {
            for (int cx = x; cx < x + w; cx++)
                blocked[cy * size + cx] = 1;
        }
    }
}
