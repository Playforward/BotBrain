using System.Numerics;
using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Detour.Io;

namespace BotBrain.Maps;

/// <summary>
/// A level's navigation mesh (Recast/Detour through DotRecast, zlib
/// licence): every floor a body can walk, upstairs and indoors included,
/// as polygons, with one-way links where a body can drop off a ledge.
/// Built offline by the map tool (BotBrain.MapTool, NavMeshBuild.cs) and
/// carried in the map package from version 3 (docs/M11-LAYERED-MAP.md).
/// <para>Positions here are the brain's: x and y on the ground, z up.
/// Recast's own are y-up; the swap is made at this class's edge and
/// nowhere else.</para>
/// </summary>
public sealed class NavMap
{
    /// <summary>The body the mesh was built for: its radius and height, the step it can climb, the drop it will take, the cell size. Written into the package.</summary>
    public sealed record Body(float Radius, float Height, float Climb, float MaxDrop, float CellSize);

    /// <summary>Detour's query object and buffers, one set per thread (two matches on one level share this map).</summary>
    private sealed class Scratch(DtNavMesh mesh)
    {
        public readonly DtNavMeshQuery Query = new(mesh);
        public readonly long[] Path = new long[4096];
        public readonly DtStraightPath[] Straight = new DtStraightPath[1024];
        public readonly long[] Visited = new long[32];
    }

    private readonly ThreadLocal<Scratch> _scratch;
    private readonly DtQueryDefaultFilter _filter = new();
    private DtNavMeshQuery _query => _scratch.Value!.Query;
    private long[] _path => _scratch.Value!.Path;
    private DtStraightPath[] _straight => _scratch.Value!.Straight;

    public DtNavMesh Mesh { get; }
    public Body BuiltFor { get; }

    public NavMap(DtNavMesh mesh, Body builtFor)
    {
        Mesh = mesh;
        BuiltFor = builtFor;
        _scratch = new ThreadLocal<Scratch>(() => new Scratch(mesh));
        _graph = new Lazy<Graph>(() => new Graph(this), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public static RcVec3f ToRecast(Vector3 p) => new(p.X, p.Z, p.Y);
    public static Vector3 FromRecast(RcVec3f p) => new(p.X, p.Z, p.Y);

    /// <summary>
    /// Switches off every polygon that cannot be walked to from any of
    /// <paramref name="starts"/> (roofs, closed rooms, the hills outside the
    /// play area), so nothing routes onto them. Returns how many stay on.
    /// </summary>
    public int KeepReachable(IEnumerable<Vector3> starts)
    {
        HashSet<long> reached = [];
        Stack<long> open = new();
        foreach (Vector3 start in starts)
        {
            // The floor under that point (1.5 m down at most), not merely the nearest: a spawn on a
            // platform must not start from the ground under it.
            if (Floor(start, out long polygon, out _, across: 3f, below: 1.5f, above: 1f) && reached.Add(polygon))
                open.Push(polygon);
        }
        while (open.Count > 0)
        {
            long current = open.Pop();
            Mesh.GetTileAndPolyByRefUnsafe(current, out DtMeshTile tile, out DtPoly poly);
            for (int link = poly.firstLink; link != DtDetour.DT_NULL_LINK; link = tile.links[link].next)
            {
                long next = tile.links[link].refs;
                if (next != 0 && reached.Add(next))
                    open.Push(next);
            }
        }
        int kept = 0;
        for (int t = 0; t < Mesh.GetMaxTiles(); t++)
        {
            DtMeshTile? tile = Mesh.GetTile(t);
            if (tile?.data?.header is not { } header)
                continue;
            long baseRef = Mesh.GetPolyRefBase(tile);
            for (int p = 0; p < header.polyCount; p++)
            {
                bool on = reached.Contains(baseRef | (long)p);
                tile.data.polys[p].flags = on ? Walkable : 0;
                kept += on && tile.data.polys[p].GetPolyType() != DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION ? 1 : 0;
            }
        }
        return kept;
    }

    /// <summary>The flag every polygon a body can reach carries; unreachable ones carry none and no query uses them.</summary>
    public const int Walkable = 1;

    /// <summary>Whether a polygon is in use (reachable from a spawn).</summary>
    public static bool InUse(DtPoly poly) => (poly.flags & Walkable) != 0;

    /// <summary>Tiles holding polygons, polygons in all, and one-way drop links.</summary>
    public (int Tiles, int Polygons, int Links) Count()
    {
        int tiles = 0, polygons = 0, links = 0;
        for (int i = 0; i < Mesh.GetMaxTiles(); i++)
        {
            if (Mesh.GetTile(i)?.data?.header is not { } header)
                continue;
            tiles++;
            polygons += header.polyCount - header.offMeshConCount;
            links += header.offMeshConCount;
        }
        return (tiles, polygons, links);
    }

    /// <summary>
    /// The walkable floor under (or nearest) a point: within <paramref name="across"/>
    /// metres sideways and from <paramref name="below"/> under it to <paramref name="above"/> over it.
    /// </summary>
    public bool Floor(Vector3 at, out long polygon, out Vector3 floor, float across = 1.5f, float below = 2.5f, float above = 0.5f)
    {
        float middle = (above - below) / 2f, half = (above + below) / 2f;
        _query.FindNearestPoly(ToRecast(at + new Vector3(0, 0, middle)), new RcVec3f(across, half, across), _filter, out polygon, out RcVec3f found, out _);
        floor = FromRecast(found);
        return polygon != 0;
    }

    /// <summary>The length of the walk from one floor point to another, or infinity if there is no way.</summary>
    public float WalkLength(Vector3 from, Vector3 to)
    {
        if (!Floor(from, out long start, out Vector3 a, across: 2f, below: 3f, above: 1f)
            || !Floor(to, out long end, out Vector3 b, across: 2f, below: 3f, above: 1f))
            return float.PositiveInfinity;
        _query.FindPath(start, end, ToRecast(a), ToRecast(b), _filter, _path, out int count, _path.Length);
        if (count == 0 || _path[count - 1] != end)
            return float.PositiveInfinity;
        _query.FindStraightPath(ToRecast(a), ToRecast(b), _path, count, _straight, out int corners, _straight.Length, 0);
        float length = 0;
        for (int i = 1; i < corners; i++)
            length += RcVec3f.Distance(_straight[i - 1].pos, _straight[i].pos);
        return length;
    }

    public void Write(BinaryWriter w)
    {
        w.Write(BuiltFor.Radius);
        w.Write(BuiltFor.Height);
        w.Write(BuiltFor.Climb);
        w.Write(BuiltFor.MaxDrop);
        w.Write(BuiltFor.CellSize);
        using MemoryStream buffer = new();
        using (BinaryWriter inner = new(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
            new DtMeshSetWriter().Write(inner, Mesh, RcByteOrder.LITTLE_ENDIAN, false);
        w.Write(buffer.Length);
        w.Write(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public static NavMap Read(BinaryReader r)
    {
        Body body = new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        long length = r.ReadInt64();
        byte[] bytes = r.ReadBytes(checked((int)length));
        using BinaryReader inner = new(new MemoryStream(bytes));
        return new NavMap(new DtMeshSetReader().Read(inner), body);
    }

    // ---- Queries for the brain and the bodies (M11 stage 2). Heights are always the feet's. ----

    /// <summary>The floor a body standing at <paramref name="feet"/> is on: from 1.5 m under its feet to 2 m over them, within a metre sideways.</summary>
    public bool PolygonAt(Vector3 feet, out long polygon, out Vector3 floor) =>
        Floor(feet, out polygon, out floor, across: 1f, below: 1.5f, above: 2f);

    /// <summary>Whether a body could stand at a point: there is a floor right under it, near that height.</summary>
    public bool Standable(Vector3 feet)
    {
        if (!Floor(feet, out _, out Vector3 floor, across: 0.3f, below: 1f, above: 1f))
            return false;
        return MathF.Abs(floor.X - feet.X) < 0.05f && MathF.Abs(floor.Y - feet.Y) < 0.05f;
    }

    /// <summary>The nearest floor to a point, within <paramref name="reach"/> sideways and 4 m up or down.</summary>
    public bool Snap(Vector3 point, float reach, out Vector3 floor) =>
        Floor(point, out _, out floor, across: reach, below: 4f, above: 4f);

    /// <summary>Whether a body can walk in a straight line along the floor from one point to another.</summary>
    public bool StraightWalk(Vector3 from, Vector3 to)
    {
        if (!PolygonAt(from, out long start, out Vector3 a))
            return false;
        Scratch s = _scratch.Value!;
        Vector3 end = new(to.X, to.Y, a.Z);
        s.Query.Raycast(start, ToRecast(a), ToRecast(end), _filter, out float t, out _, s.Path, out int count, s.Path.Length);
        if (t < 0.999f)
            return false;
        // Got there along this floor: is it the same floor the target is on?
        if (count > 0 && !float.IsNaN(to.Z) && Mesh.GetTileAndPolyByRef(s.Path[count - 1], out DtMeshTile tile, out DtPoly poly).Succeeded()
            && Mesh.GetPolyHeight(tile, poly, ToRecast(to), out float h))
            return MathF.Abs(h - to.Z) < 1.5f;
        return true;
    }

    /// <summary>A route from one point to another as its corners (the first is the next to walk to); false if there is no full route.</summary>
    public bool FindPath(Vector3 from, Vector3 to, List<Vector3> corners)
    {
        corners.Clear();
        if (!PolygonAt(from, out long start, out Vector3 a) || !Floor(to, out long end, out Vector3 b, across: 2f, below: 3f, above: 1.5f))
            return false;
        Scratch s = _scratch.Value!;
        s.Query.FindPath(start, end, ToRecast(a), ToRecast(b), _filter, s.Path, out int count, s.Path.Length);
        if (count == 0 || s.Path[count - 1] != end)
            return false;
        s.Query.FindStraightPath(ToRecast(a), ToRecast(b), s.Path, count, s.Straight, out int n, s.Straight.Length, 0);
        for (int i = 1; i < n; i++)
            corners.Add(FromRecast(s.Straight[i].pos));
        if (corners.Count == 0)
            corners.Add(b);
        return true;
    }

    /// <summary>
    /// Moves a body along the floor it is on toward a point, stopping at walls
    /// and edges (a drop is taken with <see cref="DropAt"/>). Returns where it
    /// ends up, on the floor; <paramref name="polygon"/> follows it (0 to look it up).
    /// </summary>
    public Vector3 Move(Vector3 from, Vector3 to, ref long polygon)
    {
        Scratch s = _scratch.Value!;
        if (polygon == 0 || !Mesh.IsValidPolyRef(polygon))
        {
            if (!PolygonAt(from, out polygon, out Vector3 snapped))
                return from;
            from = snapped;
        }
        s.Query.MoveAlongSurface(polygon, ToRecast(from), ToRecast(new Vector3(to.X, to.Y, from.Z)), _filter, out RcVec3f result, s.Visited, out int visited, s.Visited.Length);
        if (visited > 0)
            polygon = s.Visited[visited - 1];
        float height = result.Y;
        if (s.Query.GetPolyHeight(polygon, result, out float h).Succeeded())
            height = h;
        return new Vector3(result.X, result.Z, height);
    }

    /// <summary>
    /// A drop link starting within 0.8 m of the feet, at their height, going
    /// nearly straight the way the body is heading (within about 45°): where
    /// it lands. Looser, a body sliding along the edge of a staircase lined
    /// with short drops stepped off it sideways, again and again (a sandbox
    /// match on cosmodrome, 2026-11-17).
    /// </summary>
    public bool DropAt(Vector3 feet, Vector2 heading, out Vector3 landing)
    {
        landing = default;
        Graph g = _graph.Value;
        float best = 0.8f * 0.8f;
        bool found = false;
        foreach (int i in g.DropsNear(feet))
        {
            (Vector3 start, Vector3 end) = g.Drops[i];
            float d = (start.X - feet.X) * (start.X - feet.X) + (start.Y - feet.Y) * (start.Y - feet.Y);
            if (d > best || MathF.Abs(start.Z - feet.Z) > 1f)
                continue;
            Vector2 across = new(end.X - feet.X, end.Y - feet.Y);
            if (heading.LengthSquared() > 1e-6f && across.LengthSquared() > 1e-6f && Vector2.Dot(Vector2.Normalize(across), Vector2.Normalize(heading)) < 0.7f)
                continue;
            best = d;
            landing = end;
            found = true;
        }
        return found;
    }

    /// <summary>Walking distances from every polygon to a target, and the way to go from each: shared by everyone heading there.</summary>
    public FlowField BuildFlowField(Vector3 target, Func<Vector2, Vector3> ground) => new NavFlowField(this, _graph.Value, target, ground);

    private readonly Lazy<Graph> _graph;

    // ---- For the map tool and the recorder: the polygons in use as places. ----

    /// <summary>How many polygons are in use (offline analysis numbers them 0 to this).</summary>
    public int Places => _graph.Value.Refs.Length;

    /// <summary>The middle of polygon <paramref name="place"/>, on its floor.</summary>
    public Vector3 PlaceCentre(int place) => _graph.Value.Centre[place];

    /// <summary>Whether a place is a drop link rather than a floor.</summary>
    public bool IsDrop(int place) => Mesh.GetTileAndPolyByRef(_graph.Value.Refs[place], out _, out DtPoly poly).Succeeded()
        && poly.GetPolyType() == DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION;

    /// <summary>The place a body standing at <paramref name="feet"/> is on, or -1.</summary>
    public int PlaceAt(Vector3 feet) =>
        PolygonAt(feet, out long polygon, out _) && _graph.Value.Index.TryGetValue(polygon, out int place) ? place : -1;

    /// <summary>
    /// Walking distance from a start to every place (infinity where it cannot
    /// be walked to), off drops but never back up them. For offline use.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public float[] DistancesFrom(Vector3 start)
    {
        Graph g = _graph.Value;
        float[] distance = new float[g.Refs.Length];
        Array.Fill(distance, float.PositiveInfinity);
        int first = PlaceAt(start);
        if (first < 0)
            return distance;
        PriorityQueue<int, float> open = new();
        distance[first] = 0;
        open.Enqueue(first, 0);
        while (open.TryDequeue(out int u, out float du))
        {
            if (du > distance[u])
                continue;
            for (int k = g.OutStart[u]; k < g.OutStart[u + 1]; k++)
            {
                int v = g.OutTo[k];
                float dv = du + g.OutCost[k];
                if (dv >= distance[v])
                    continue;
                distance[v] = dv;
                open.Enqueue(v, dv);
            }
        }
        return distance;
    }

    /// <summary>
    /// The floors from above, on a grid of square cells: for each cell the
    /// highest floor in use and the lowest (NaN where there is none), and the
    /// starts of the drop links. For pictures and the replay viewer.
    /// </summary>
    public (float[] Top, float[] Low, List<Vector3> Drops) FloorsFromAbove(Vector2 origin, float cell, int width, int height)
    {
        float[] top = new float[width * height], low = new float[width * height];
        Array.Fill(top, float.NaN);
        Array.Fill(low, float.NaN);
        List<Vector3> drops = [];
        int CellX(float x) => (int)MathF.Floor((x - origin.X) / cell);
        int CellY(float y) => (int)MathF.Floor((y - origin.Y) / cell);
        for (int t = 0; t < Mesh.GetMaxTiles(); t++)
        {
            if (Mesh.GetTile(t)?.data is not { header: not null } data)
                continue;
            for (int p = 0; p < data.header.polyCount; p++)
            {
                DtPoly poly = data.polys[p];
                if (!InUse(poly))
                    continue;
                Vector3 V(int k) => FromRecast(new RcVec3f(data.verts[poly.verts[k] * 3], data.verts[poly.verts[k] * 3 + 1], data.verts[poly.verts[k] * 3 + 2]));
                if (poly.GetPolyType() == DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION)
                {
                    drops.Add(V(0));
                    continue;
                }
                for (int k = 2; k < poly.vertCount; k++)
                {
                    Vector3 a = V(0), b = V(k - 1), c = V(k);
                    int x0 = Math.Max(0, CellX(MathF.Min(a.X, MathF.Min(b.X, c.X)))), x1 = Math.Min(width - 1, CellX(MathF.Max(a.X, MathF.Max(b.X, c.X))));
                    int y0 = Math.Max(0, CellY(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)))), y1 = Math.Min(height - 1, CellY(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
                    float d = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                    if (MathF.Abs(d) < 1e-6f)
                        continue;
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                        {
                            Vector2 q = new(origin.X + (x + 0.5f) * cell, origin.Y + (y + 0.5f) * cell);
                            float u = ((b.X - q.X) * (c.Y - q.Y) - (b.Y - q.Y) * (c.X - q.X)) / d;
                            float v = ((c.X - q.X) * (a.Y - q.Y) - (c.Y - q.Y) * (a.X - q.X)) / d;
                            if (u < 0 || v < 0 || u + v > 1)
                                continue;
                            float z = u * a.Z + v * b.Z + (1 - u - v) * c.Z;
                            int i = y * width + x;
                            top[i] = float.IsNaN(top[i]) ? z : MathF.Max(top[i], z);
                            low[i] = float.IsNaN(low[i]) ? z : MathF.Min(low[i], z);
                        }
                }
            }
        }
        return (top, low, drops);
    }

    /// <summary>The polygons in use as a graph: an index for each, its centre, its ways out and in (drops one way), and the drop links by place.</summary>
    internal sealed class Graph
    {
        public readonly long[] Refs;
        public readonly Dictionary<long, int> Index = [];
        public readonly Vector3[] Centre;
        public readonly int[] InStart;
        public readonly int[] InFrom;
        public readonly float[] InCost;
        public readonly int[] OutStart;
        public readonly int[] OutTo;
        public readonly float[] OutCost;
        public readonly List<(Vector3 Start, Vector3 End)> Drops = [];
        private readonly Dictionary<long, List<int>> _dropCells = [];

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        public Graph(NavMap nav)
        {
            DtNavMesh mesh = nav.Mesh;
            List<long> refs = [];
            for (int t = 0; t < mesh.GetMaxTiles(); t++)
            {
                DtMeshTile? tile = mesh.GetTile(t);
                if (tile?.data?.header is not { } header)
                    continue;
                long baseRef = mesh.GetPolyRefBase(tile);
                for (int p = 0; p < header.polyCount; p++)
                {
                    if (!InUse(tile.data.polys[p]))
                        continue;
                    long r = baseRef | (long)p;
                    Index[r] = refs.Count;
                    refs.Add(r);
                    if (tile.data.polys[p].GetPolyType() == DtPolyTypes.DT_POLYTYPE_OFFMESH_CONNECTION)
                    {
                        DtPoly poly = tile.data.polys[p];
                        Vector3 a = FromRecast(new RcVec3f(tile.data.verts[poly.verts[0] * 3], tile.data.verts[poly.verts[0] * 3 + 1], tile.data.verts[poly.verts[0] * 3 + 2]));
                        Vector3 b = FromRecast(new RcVec3f(tile.data.verts[poly.verts[1] * 3], tile.data.verts[poly.verts[1] * 3 + 1], tile.data.verts[poly.verts[1] * 3 + 2]));
                        int drop = Drops.Count;
                        Drops.Add((a, b));
                        long cell = Cell(a);
                        if (!_dropCells.TryGetValue(cell, out List<int>? list))
                            _dropCells[cell] = list = [];
                        list.Add(drop);
                    }
                }
            }
            Refs = [.. refs];
            Centre = new Vector3[Refs.Length];
            List<(int From, int To, float Cost)> edges = [];
            for (int i = 0; i < Refs.Length; i++)
            {
                Centre[i] = FromRecast(mesh.GetPolyCenter(Refs[i]));
                mesh.GetTileAndPolyByRefUnsafe(Refs[i], out DtMeshTile tile, out DtPoly poly);
                for (int link = poly.firstLink; link != DtDetour.DT_NULL_LINK; link = tile.links[link].next)
                {
                    if (Index.TryGetValue(tile.links[link].refs, out int to))
                        edges.Add((i, to, 0));
                }
            }
            // Ways in to each polygon, for walking distances back from a target.
            InStart = new int[Refs.Length + 1];
            foreach ((int _, int to, float _) in edges)
                InStart[to + 1]++;
            for (int i = 0; i < Refs.Length; i++)
                InStart[i + 1] += InStart[i];
            InFrom = new int[edges.Count];
            InCost = new float[edges.Count];
            int[] fill = (int[])InStart.Clone();
            foreach ((int from, int to, float _) in edges)
            {
                int k = fill[to]++;
                InFrom[k] = from;
                InCost[k] = Vector3.Distance(Centre[from], Centre[to]);
            }
            // And ways out, for walking distances forward from a place.
            OutStart = new int[Refs.Length + 1];
            foreach ((int from, int _, float _) in edges)
                OutStart[from + 1]++;
            for (int i = 0; i < Refs.Length; i++)
                OutStart[i + 1] += OutStart[i];
            OutTo = new int[edges.Count];
            OutCost = new float[edges.Count];
            fill = (int[])OutStart.Clone();
            foreach ((int from, int to, float _) in edges)
            {
                int k = fill[from]++;
                OutTo[k] = to;
                OutCost[k] = Vector3.Distance(Centre[from], Centre[to]);
            }
        }

        private static long Cell(Vector3 p) => ((long)MathF.Floor(p.X / 4f) << 32) ^ (uint)(int)MathF.Floor(p.Y / 4f);

        public IEnumerable<int> DropsNear(Vector3 p)
        {
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (_dropCells.TryGetValue(Cell(p + new Vector3(dx * 4f, dy * 4f, 0)), out List<int>? list))
                        foreach (int i in list)
                            yield return i;
        }
    }

    /// <summary>
    /// A flow field over the mesh: walking distance from every polygon to the
    /// target and the next polygon toward it. A body steers at the furthest
    /// corner of the straight-line route along that corridor, and over a drop
    /// link when the route goes down one.
    /// </summary>
    private sealed class NavFlowField : FlowField
    {
        private readonly NavMap _nav;
        private readonly Graph _graph;
        private readonly float[] _distance;
        private readonly int[] _next;
        private readonly int _goal = -1;
        private readonly Vector3 _target;
        private readonly Func<Vector2, Vector3> _ground;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        public NavFlowField(NavMap nav, Graph graph, Vector3 target, Func<Vector2, Vector3> ground)
        {
            _nav = nav;
            _graph = graph;
            _ground = ground;
            _target = target;
            int n = graph.Refs.Length;
            _distance = new float[n];
            _next = new int[n];
            Array.Fill(_distance, float.PositiveInfinity);
            Array.Fill(_next, -1);
            if (!nav.Floor(target, out long goalRef, out Vector3 floor, across: 2f, below: 3f, above: 1.5f) || !graph.Index.TryGetValue(goalRef, out _goal))
            {
                _goal = -1;
                return;
            }
            _target = floor;
            PriorityQueue<int, float> open = new();
            _distance[_goal] = Vector3.Distance(floor, graph.Centre[_goal]);
            open.Enqueue(_goal, _distance[_goal]);
            while (open.TryDequeue(out int u, out float du))
            {
                if (du > _distance[u])
                    continue;
                for (int k = graph.InStart[u]; k < graph.InStart[u + 1]; k++)
                {
                    int v = graph.InFrom[k];
                    float dv = du + graph.InCost[k];
                    if (dv >= _distance[v])
                        continue;
                    _distance[v] = dv;
                    _next[v] = u;
                    open.Enqueue(v, dv);
                }
            }
        }

        public override Vector2 Target => new(_target.X, _target.Y);
        public override float TargetHeight => _target.Z;
        protected override Vector3 Ground(Vector2 p) => _ground(p);

        public override float DistanceFrom(Vector3 position)
        {
            if (_goal < 0 || !_nav.PolygonAt(position, out long r, out _) || !_graph.Index.TryGetValue(r, out int i))
                return float.PositiveInfinity;
            return _distance[i];
        }

        public override Vector2 SteerPoint(Vector3 position, int lookahead = 10)
        {
            Vector2 here = new(position.X, position.Y);
            if (_goal < 0 || !_nav.PolygonAt(position, out long r, out Vector3 floor) || !_graph.Index.TryGetValue(r, out int i) || float.IsInfinity(_distance[i]))
                return here;
            if (i == _goal)
                return Target;
            Scratch s = _nav._scratch.Value!;
            int count = 0, last = i;
            for (int at = i; at >= 0 && count < 48; at = _next[at])
            {
                s.Path[count++] = _graph.Refs[at];
                last = at;
                if (at == _goal)
                    break;
            }
            Vector3 end = last == _goal ? _target : _graph.Centre[last];
            s.Query.FindStraightPath(ToRecast(floor), ToRecast(end), s.Path, count, s.Straight, out int corners, 8, 0);
            if (corners < 2)
                return new Vector2(end.X, end.Y);
            DtStraightPath next = s.Straight[1];
            Vector3 c = FromRecast(next.pos);
            // At the top of a drop the way on is its far end: step off.
            if ((next.flags & DtStraightPathFlags.DT_STRAIGHTPATH_OFFMESH_CONNECTION) != 0 && corners > 2
                && Vector2.DistanceSquared(here, new Vector2(c.X, c.Y)) < 0.75f * 0.75f)
                c = FromRecast(s.Straight[2].pos);
            return new Vector2(c.X, c.Y);
        }
    }
}
