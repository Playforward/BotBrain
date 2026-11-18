using System.Numerics;

namespace BotBrain.Maps;

public readonly record struct MapSeed(Vector3 Position, byte Team, int Id);

public sealed class MeshMapOptions
{
    public float CellSize { get; set; } = 0.5f;

    /// <summary>Clear height a standing body needs.</summary>
    public float AgentHeight { get; set; } = 1.7f;

    /// <summary>Most the ground may rise or fall between neighbouring cell centres.</summary>
    public float StepHeight { get; set; } = 0.45f;

    /// <summary>How level a surface must be to stand on: the cosine of the steepest slope (0.64 is about 50 degrees).</summary>
    public float MinLevelness { get; set; } = 0.64f;

    /// <summary>Heights above the ground at which the way to a neighbour must be clear. Anything lower can be stepped over.</summary>
    public float[] ProbeHeights { get; set; } = [0.5f, 1.0f, 1.5f];

    /// <summary>
    /// How far to either side of the line between two cell centres the way
    /// must also be clear, so a body has room for its shoulders. Small enough
    /// that a one-metre doorway always passes on a half-metre grid.
    /// </summary>
    public float SideClearance { get; set; } = 0.25f;

    /// <summary>
    /// Farthest a body may drop off an edge. A drop is one-way: the cell
    /// below is reachable from above but not the reverse. Spawn platforms in
    /// MAG are left this way.
    /// </summary>
    public float MaxDrop { get; set; } = 4f;

    /// <summary>
    /// How far from the line between the teams' spawn areas the map reaches,
    /// in metres; 0 for no limit. A stand-in for the level's real boundary,
    /// which is not decoded: without it the map takes in every hill around
    /// the battlefield.
    /// </summary>
    public float PlayRadius { get; set; } = 130f;

    /// <summary>Points spread over the walkable area for bots to head for when nothing else calls.</summary>
    public int InterestPoints { get; set; } = 16;

    /// <summary>
    /// Interest points go only where walking from one side's spawns to the
    /// other's through that place is at most this much longer than the direct
    /// way (0.35 = 35% longer): the lanes, flanks and middle where players
    /// meet, not the empty edges of the play area. Needs spawns of two sides;
    /// without them points spread over everything.
    /// </summary>
    public float InterestRouteSlack { get; set; } = 0.35f;

    /// <summary>Interest points stay at least this far, walking, from either side's spawn.</summary>
    public float InterestSpawnClearance { get; set; } = 20f;

    /// <summary>
    /// Vantage points to find: places on the routes that see the most of the
    /// routes between 10 and 70 m away, at least 25 m apart. 0 for none.
    /// </summary>
    public int VantagePoints { get; set; } = 12;
}

public sealed class MeshMapReport
{
    public int Seeds, SeedsOnGround, Cells, Width, Height;
    public float MinGround, MaxGround;
    public double Seconds;

    /// <summary>Why steps to unreached neighbours were refused: nothing to stand on, too high or low, or something in the way at each probe height.</summary>
    public int NoSurface, TooSteep, Drops;
    /// <summary>Walkable area interest points were chosen from, square metres, and whether it was limited to the routes between the sides.</summary>
    public float InterestArea;
    public bool InterestOnRoutes;
    /// <summary>Vantage points found, and how much of the route sample the best and the worst of them see.</summary>
    public int Vantages;
    public float VantageBestShare, VantageWorstShare;
    public int[] Blocked = [];
    public SortedDictionary<int, int> StepHistogram = [];
}

/// <summary>
/// Turns a level's collision mesh into a walkable grid: everywhere a body
/// can walk to from the seed positions (spawn points), one surface per cell,
/// with the cell's ground height and which neighbours it connects to.
/// One layer only: where two floors are both reachable, the one found first
/// wins and the other does not exist for the bots.
/// </summary>
public static class MeshMapBuilder
{
    private static readonly int[] Dx = [1, -1, 0, 0, 1, 1, -1, -1];
    private static readonly int[] Dy = [0, 0, 1, -1, 1, -1, 1, -1];

    private sealed class Columns(TriangleMesh mesh, MeshMapOptions options)
    {
        private readonly Dictionary<long, (int Start, int Count)> _index = [];
        private readonly List<float> _heights = [];
        private readonly float[] _h = new float[256], _l = new float[256];

        /// <summary>
        /// Where a body stepping off an edge at height <paramref name="from"/> lands in a cell:
        /// the first surface below, if it can be stood on and nothing is in the way down.
        /// </summary>
        public float Landing(int cx, int cy, float from)
        {
            float x = (cx + 0.5f) * options.CellSize, y = (cy + 0.5f) * options.CellSize;
            int n = mesh.Column(x, y, _h, _l);
            int top = -1;
            for (int i = 0; i < n; i++)
            {
                if (_h[i] >= from + options.AgentHeight)
                    continue; // overhead, clear of the body
                if (top < 0 || _h[i] > _h[top])
                    top = i;
            }
            if (top < 0 || _l[top] < options.MinLevelness)
                return float.NaN;
            float z = _h[top];
            return z < from - options.StepHeight && z >= from - options.MaxDrop ? z : float.NaN;
        }

        /// <summary>Heights a body can stand at in a cell: level enough, with head room above.</summary>
        public ReadOnlySpan<float> Standable(int cx, int cy)
        {
            long key = Key(cx, cy);
            if (!_index.TryGetValue(key, out (int Start, int Count) entry))
            {
                float x = (cx + 0.5f) * options.CellSize, y = (cy + 0.5f) * options.CellSize;
                int n = mesh.Column(x, y, _h, _l);
                int start = _heights.Count;
                for (int i = 0; i < n; i++)
                {
                    if (_l[i] < options.MinLevelness)
                        continue;
                    bool clear = true;
                    for (int j = 0; j < n && clear; j++)
                        clear = !(_h[j] > _h[i] + 0.35f && _h[j] < _h[i] + options.AgentHeight);
                    if (clear)
                        _heights.Add(_h[i]);
                }
                entry = (start, _heights.Count - start);
                _index[key] = entry;
            }
            return System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_heights).Slice(entry.Start, entry.Count);
        }
    }

    private static long Key(int cx, int cy) => ((long)cx << 32) | (uint)cy;

    public static GridMap Build(string id, TriangleMesh mesh, IReadOnlyList<MapSeed> seeds, MeshMapOptions options, out MeshMapReport report,
        IReadOnlyList<MapPoint>? extraPoints = null)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        float cell = options.CellSize;
        Columns columns = new(mesh, options);
        Dictionary<long, float> ground = [];
        Dictionary<long, byte> links = [];
        Queue<(int X, int Y)> queue = new();
        report = new MeshMapReport { Seeds = seeds.Count, Blocked = new int[options.ProbeHeights.Length] };

        // The play area: within PlayRadius of the line from one team's spawns to the other's.
        Vector2 endA = default, endB = default;
        {
            Vector2 sumA = default, sumB = default;
            int countA = 0, countB = 0;
            byte first = seeds.Count > 0 ? seeds[0].Team : (byte)0;
            foreach (MapSeed seed in seeds)
            {
                Vector2 p = new(seed.Position.X, seed.Position.Y);
                if (seed.Team == first)
                {
                    sumA += p;
                    countA++;
                }
                else
                {
                    sumB += p;
                    countB++;
                }
            }
            endA = countA > 0 ? sumA / countA : default;
            endB = countB > 0 ? sumB / countB : endA;
        }
        bool InPlay(int cx, int cy)
        {
            if (options.PlayRadius <= 0)
                return true;
            Vector2 p = new((cx + 0.5f) * cell, (cy + 0.5f) * cell), axis = endB - endA;
            float t = axis.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(p - endA, axis) / axis.LengthSquared(), 0, 1) : 0;
            return Vector2.DistanceSquared(p, endA + axis * t) <= options.PlayRadius * options.PlayRadius;
        }

        foreach (MapSeed seed in seeds)
        {
            int sx = (int)MathF.Floor(seed.Position.X / cell), sy = (int)MathF.Floor(seed.Position.Y / cell);
            bool placed = false;
            for (int r = 0; r <= 3 && !placed; r++)
            {
                for (int y = sy - r; y <= sy + r && !placed; y++)
                {
                    for (int x = sx - r; x <= sx + r && !placed; x++)
                    {
                        // A spawn point floats a little above its floor: take the nearest surface at or below it.
                        float z = Nearest(columns.Standable(x, y), seed.Position.Z - 0.6f, 1.5f);
                        if (float.IsNaN(z))
                            continue;
                        placed = true;
                        if (ground.TryAdd(Key(x, y), z))
                            queue.Enqueue((x, y));
                    }
                }
            }
            if (placed)
                report.SeedsOnGround++;
        }

        // Walk outward along the four directions. A neighbour is reached if it has a
        // surface close to this height and nothing solid stands between the two.
        while (queue.Count > 0)
        {
            (int cx, int cy) = queue.Dequeue();
            float z = ground[Key(cx, cy)];
            Vector2 from = new((cx + 0.5f) * cell, (cy + 0.5f) * cell);
            for (int d = 0; d < 4; d++)
            {
                int nx = cx + Dx[d], ny = cy + Dy[d];
                if (!InPlay(nx, ny))
                    continue;
                long key = Key(nx, ny);
                bool known = ground.TryGetValue(key, out float nz);
                if (!known)
                {
                    nz = Nearest(columns.Standable(nx, ny), z, options.StepHeight);
                    if (float.IsNaN(nz))
                    {
                        // For the report: how far off was the nearest surface, if there was one?
                        float any = Nearest(columns.Standable(nx, ny), z, 4f);
                        if (float.IsNaN(any))
                            report.NoSurface++;
                        else
                        {
                            report.TooSteep++;
                            int bin = (int)MathF.Round((any - z) * 4);
                            report.StepHistogram[bin] = report.StepHistogram.GetValueOrDefault(bin) + 1;
                        }
                    }
                }
                Vector2 to = new((nx + 0.5f) * cell, (ny + 0.5f) * cell);
                if (float.IsNaN(nz) || MathF.Abs(nz - z) > options.StepHeight)
                {
                    // Not a step. Is it a drop: clear at this height, and somewhere to land below?
                    float landing = columns.Landing(nx, ny, z);
                    if (float.IsNaN(landing) || (known && MathF.Abs(ground[key] - landing) > 0.05f))
                        continue;
                    bool open = true;
                    foreach (float above in options.ProbeHeights)
                        open &= Open(mesh, from, to, z + above, z + above, options.SideClearance);
                    if (!open)
                        continue;
                    links[Key(cx, cy)] = (byte)(links.GetValueOrDefault(Key(cx, cy)) | (1 << d));
                    report.Drops++;
                    if (!known)
                    {
                        ground[key] = landing;
                        queue.Enqueue((nx, ny));
                    }
                    continue;
                }
                bool clear = true;
                for (int probe = 0; probe < options.ProbeHeights.Length; probe++)
                {
                    float above = options.ProbeHeights[probe];
                    if (!Open(mesh, from, to, z + above, nz + above, options.SideClearance))
                    {
                        clear = false;
                        if (!known)
                            report.Blocked[probe]++;
                        break;
                    }
                }
                if (!clear)
                    continue;
                links[Key(cx, cy)] = (byte)(links.GetValueOrDefault(Key(cx, cy)) | (1 << d));
                links[key] = (byte)(links.GetValueOrDefault(key) | (1 << (d ^ 1)));
                if (!known)
                {
                    ground[key] = nz;
                    queue.Enqueue((nx, ny));
                }
            }
        }

        if (ground.Count == 0)
            throw new InvalidOperationException("no seed position stands on walkable ground");

        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (long key in ground.Keys)
        {
            int x = (int)(key >> 32), y = (int)key;
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }
        minX -= 2;
        minY -= 2;
        int width = maxX - minX + 3, height = maxY - minY + 3;
        byte[] blocked = new byte[width * height];
        Array.Fill(blocked, (byte)1);
        float[] heights = new float[width * height];
        byte[] cellLinks = new byte[width * height];
        float low = float.MaxValue, high = float.MinValue;
        foreach ((long key, float z) in ground)
        {
            int i = ((int)key - minY) * width + (int)(key >> 32) - minX;
            blocked[i] = 0;
            heights[i] = z;
            cellLinks[i] = links.GetValueOrDefault(key);
            low = MathF.Min(low, z);
            high = MathF.Max(high, z);
        }
        // A diagonal step is allowed where both ways round the corner are.
        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int i = y * width + x;
                if (blocked[i] != 0)
                    continue;
                for (int d = 4; d < 8; d++)
                {
                    int bitX = Dx[d] > 0 ? 0 : 1, bitY = Dy[d] > 0 ? 2 : 3;
                    int viaX = i + Dx[d], viaY = i + Dy[d] * width;
                    if ((cellLinks[i] & (1 << bitX)) != 0 && (cellLinks[i] & (1 << bitY)) != 0
                        && (cellLinks[viaX] & (1 << bitY)) != 0 && (cellLinks[viaY] & (1 << bitX)) != 0)
                        cellLinks[i] |= (byte)(1 << d);
                }
            }
        }

        Vector2 origin = new(minX * cell, minY * cell);
        List<MapPoint> points = [];
        foreach (MapSeed seed in seeds)
            points.Add(new MapPoint(MapPointKind.Spawn, seed.Team, seed.Id, new Vector2(seed.Position.X, seed.Position.Y), 2f));
        int extra = 0;
        foreach (MapPoint p in extraPoints ?? [])
        {
            // Only the ones that ended up on walkable ground are any use.
            int px = (int)MathF.Floor((p.Position.X - origin.X) / cell), py = (int)MathF.Floor((p.Position.Y - origin.Y) / cell);
            if (px < 0 || py < 0 || px >= width || py >= height || blocked[py * width + px] != 0)
                continue;
            points.Add(p with { Id = 1000 + extra++ });
        }
        // Route distances need the finished grid; a first copy without interest points serves for that.
        GridMap layout = new(id, width, height, cell, blocked, [.. points], origin, heights, cellLinks, mesh);
        List<int> band = RouteBand(layout, seeds, options, out List<float> balance);
        points.AddRange(Spread(layout, band, balance, options, report));
        points.AddRange(Vantages(layout, band, options, report));

        report.Cells = ground.Count;
        report.Width = width;
        report.Height = height;
        report.MinGround = low;
        report.MaxGround = high;
        report.Seconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
        return new GridMap(id, width, height, cell, blocked, points, origin, heights, cellLinks, mesh);
    }

    /// <summary>Whether the straight way between two points, and the same way shifted to either side, is clear.</summary>
    private static bool Open(TriangleMesh mesh, Vector2 from, Vector2 to, float fromZ, float toZ, float side)
    {
        Vector2 along = Vector2.Normalize(to - from);
        Vector2 across = new Vector2(-along.Y, along.X) * side;
        return mesh.LineOfSight(new Vector3(from, fromZ), new Vector3(to, toZ))
            && mesh.LineOfSight(new Vector3(from + across, fromZ), new Vector3(to + across, toZ))
            && mesh.LineOfSight(new Vector3(from - across, fromZ), new Vector3(to - across, toZ));
    }

    private static float Nearest(ReadOnlySpan<float> heights, float target, float within)
    {
        float best = float.NaN, bestDistance = within;
        foreach (float h in heights)
        {
            float d = MathF.Abs(h - target);
            if (d <= bestDistance)
            {
                bestDistance = d;
                best = h;
            }
        }
        return best;
    }

    /// <summary>
    /// Points as far from each other as the area where the sides meet allows.
    /// Spreading over all walkable ground instead put most of them on the rim
    /// of the play area (PlayRadius), where nobody goes, and bots roamed
    /// there.
    /// </summary>
    private static List<MapPoint> Spread(GridMap map, List<int> onRoutes, List<float> balance, MeshMapOptions options, MeshMapReport report)
    {
        int width = map.Width, count = options.InterestPoints;
        List<int> free = EveryOtherFreeCell(map);
        List<MapPoint> result = [];
        if (free.Count == 0)
            return result;
        int pick = free.Count / 2;
        if (onRoutes.Count >= count * 8)
        {
            free = onRoutes;
            // Start in the middle, as far from both sides as from each other.
            pick = balance.IndexOf(balance.Min());
            report.InterestOnRoutes = true;
        }
        report.InterestArea = free.Count * 4 * map.CellSize * map.CellSize;

        float[] nearest = new float[free.Count];
        Array.Fill(nearest, float.MaxValue);
        for (int n = 0; n < count; n++)
        {
            int px = free[pick] % width, py = free[pick] / width;
            result.Add(new MapPoint(MapPointKind.Interest, 255, n, map.CellCenter(px, py), 3f));
            float far = -1;
            for (int i = 0; i < free.Count; i++)
            {
                float dx = free[i] % width - px, dy = free[i] / width - py;
                nearest[i] = MathF.Min(nearest[i], dx * dx + dy * dy);
                if (nearest[i] > far)
                {
                    far = nearest[i];
                    pick = i;
                }
            }
        }
        return result;
    }

    /// <summary>Every fourth cell is plenty to choose from.</summary>
    private static List<int> EveryOtherFreeCell(GridMap map)
    {
        List<int> free = [];
        for (int y = 0; y < map.Height; y += 2)
            for (int x = 0; x < map.Width; x += 2)
                if (!map.IsBlockedCell(x, y))
                    free.Add(y * map.Width + x);
        return free;
    }

    /// <summary>
    /// The cells (every fourth) on the ways between the two sides' spawns, with
    /// how unevenly each lies between them. Empty without spawns of two
    /// connected sides.
    /// </summary>
    private static List<int> RouteBand(GridMap map, IReadOnlyList<MapSeed> seeds, MeshMapOptions options, out List<float> balance)
    {
        List<int> free = EveryOtherFreeCell(map);
        List<int> onRoutes = [];
        balance = [];
        // The two sides: the first seed's team, and everyone else (as for the play area).
        if (SideAnchors(map, seeds, out Vector2 home, out Vector2 away))
        {
            // Walking out from each side's spawn. Spawns may stand on platforms that can be left but not
            // climbed back onto, so "from the spawn" is the only direction that is always defined. Where the
            // two sides' walks meet is the front; the shortest meeting is the direct way between them.
            float[] fromHome = map.DistancesFrom(home), fromAway = map.DistancesFrom(away);
            float direct = float.PositiveInfinity;
            foreach (int c in free)
                direct = MathF.Min(direct, fromHome[c] + fromAway[c]);
            if (!float.IsInfinity(direct) && direct > 0)
            {
                foreach (int c in free)
                {
                    float a = fromHome[c], b = fromAway[c];
                    if (float.IsInfinity(a) || float.IsInfinity(b) || a + b > direct * (1 + options.InterestRouteSlack)
                        || a < options.InterestSpawnClearance || b < options.InterestSpawnClearance)
                        continue;
                    onRoutes.Add(c);
                    balance.Add(MathF.Abs(a - b));
                }
            }
        }
        return onRoutes;
    }

    /// <summary>
    /// Places a camper would choose: on the routes, seeing as much of the
    /// routes 10 to 70 m away as possible, better with something solid close
    /// by to put its back to. Eye 1.6 m above the ground, looking at chest
    /// height (1.2 m). Greedy by score, at least 25 m apart.
    /// </summary>
    private static List<MapPoint> Vantages(GridMap map, List<int> band, MeshMapOptions options, MeshMapReport report)
    {
        List<MapPoint> result = [];
        if (options.VantagePoints <= 0 || band.Count == 0)
            return result;
        List<Vector2> targets = Thin(map, band, 6f), candidates = Thin(map, band, 3f);
        // A fixed stride keeps the target sample even and the build repeatable.
        if (targets.Count > 600)
            targets = [.. targets.Where((_, k) => k % (targets.Count / 600 + 1) == 0)];
        Vector3[] chests = [.. targets.Select(t => new Vector3(t, map.GroundAt(t) + 1.2f))];
        float[] score = new float[candidates.Count];
        int[] seen = new int[candidates.Count];
        Parallel.For(0, candidates.Count, c =>
        {
            Vector2 at = candidates[c];
            Vector3 eye = new(at, map.GroundAt(at) + 1.6f);
            int count = 0;
            foreach (Vector3 chest in chests)
            {
                float d2 = Vector2.DistanceSquared(at, new Vector2(chest.X, chest.Y));
                if (d2 < 10f * 10f || d2 > 70f * 70f)
                    continue;
                if (map.LineOfSight(eye, chest))
                    count++;
            }
            int cover = 0;
            for (int k = 0; k < 8; k++)
            {
                float angle = k * MathF.PI / 4;
                if (map.IsBlocked(at + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 1.2f))
                    cover++;
            }
            seen[c] = count;
            // Some cover helps; a spot boxed in on most sides is a cupboard, not a vantage.
            score[c] = cover >= 6 ? 0 : count * (1f + 0.12f * Math.Min(cover, 3));
        });
        int[] order = [.. Enumerable.Range(0, candidates.Count).OrderByDescending(c => score[c])];
        foreach (int c in order)
        {
            if (result.Count >= options.VantagePoints || score[c] <= 0)
                break;
            if (result.Any(p => Vector2.DistanceSquared(p.Position, candidates[c]) < 25f * 25f))
                continue;
            result.Add(new MapPoint(MapPointKind.Vantage, 255, result.Count, candidates[c], 2f));
            float share = seen[c] / (float)Math.Max(1, chests.Length);
            report.VantageBestShare = result.Count == 1 ? share : report.VantageBestShare;
            report.VantageWorstShare = share;
        }
        report.Vantages = result.Count;
        return result;
    }

    /// <summary>The cells' centres, keeping one per square of the given size.</summary>
    private static List<Vector2> Thin(GridMap map, List<int> cells, float spacing)
    {
        HashSet<long> taken = [];
        List<Vector2> kept = [];
        foreach (int c in cells)
        {
            Vector2 p = map.CellCenter(c);
            long key = ((long)MathF.Floor(p.X / spacing) << 32) ^ (uint)(int)MathF.Floor(p.Y / spacing);
            if (taken.Add(key))
                kept.Add(p);
        }
        return kept;
    }

    /// <summary>
    /// For each side (the first seed's team, and the rest), the spawn on
    /// walkable ground nearest that side's middle. False unless both sides
    /// have one.
    /// </summary>
    private static bool SideAnchors(GridMap map, IReadOnlyList<MapSeed> seeds, out Vector2 home, out Vector2 away)
    {
        home = away = default;
        if (seeds.Count == 0)
            return false;
        byte first = seeds[0].Team;
        bool found = true;
        for (int side = 0; side < 2; side++)
        {
            List<Vector2> mine = [.. seeds.Where(s => (s.Team == first) == (side == 0)).Select(s => new Vector2(s.Position.X, s.Position.Y))];
            Vector2 middle = mine.Count > 0 ? mine.Aggregate(Vector2.Zero, (sum, p) => sum + p) / mine.Count : default;
            Vector2? best = null;
            foreach (Vector2 p in mine.OrderBy(p => Vector2.DistanceSquared(p, middle)))
            {
                if (map.NearestFree(p, out int cx, out int cy))
                {
                    best = map.CellCenter(cx, cy);
                    break;
                }
            }
            if (best is null)
                found = false;
            else if (side == 0)
                home = best.Value;
            else
                away = best.Value;
        }
        return found;
    }
}
