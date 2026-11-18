using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace BotBrain.Maps;

public enum MapPointKind : byte
{
    Spawn = 1,
    Zone = 2,
    Interest = 3,
    /// <summary>A spot with a long view over the routes and, usually, something at its back: where a camper waits.</summary>
    Vantage = 4,
}

/// <param name="Height">The floor's height at the point (package version 4 on); NaN where only the grid's ground is known.</param>
public readonly record struct MapPoint(MapPointKind Kind, byte Team, int Id, Vector2 Position, float Radius, float Height = float.NaN);

/// <summary>
/// The simplest map package: a flat grid of free and blocked cells with
/// annotated points. It answers the three questions the brain and the body
/// kit ask of any map: can A see B, how do I get from A to B, and where does
/// a ray stop. A navigation-mesh package answers the same questions for real
/// levels (spec section 9).
/// Not thread-safe: path queries share scratch buffers.
/// </summary>
public sealed class GridMap
{
    public const float AgentRadius = 0.35f;
    private const uint Magic = 0x4D474242; // "BBGM"

    private static readonly int[] Dx = [1, -1, 0, 0, 1, 1, -1, -1];
    private static readonly int[] Dy = [0, 0, 1, -1, 1, -1, 1, -1];
    private static readonly int[] Opposite = [1, 0, 3, 2, 7, 6, 5, 4];

    private readonly byte[] _blocked;
    private readonly List<MapPoint> _points;
    private readonly float[]? _ground;
    private readonly byte[]? _links;

    private int[]? _g, _stamp, _parent;
    private int _generation;
    private readonly PriorityQueue<int, int> _open = new();
    private readonly List<int> _cells = [];

    public string Id { get; }
    public int Width { get; }
    public int Height { get; }
    public float CellSize { get; }

    /// <summary>World position of the corner of cell (0, 0).</summary>
    public Vector2 Origin { get; }

    /// <summary>The level's collision, for sight lines and shots in three dimensions. Null for a flat map.</summary>
    public TriangleMesh? Mesh { get; }

    /// <summary>The navigation mesh: every floor, upstairs included (package version 3). Null in older packages.</summary>
    public NavMap? Nav { get; }

    /// <summary>Whether cells carry a ground height and say which neighbours can be walked to.</summary>
    public bool HasGround => _ground is not null;

    /// <summary>
    /// How much room a body is given against blocked cells. On a map built
    /// from a level the cells themselves were only marked walkable where a
    /// body has room, so almost none is needed on top; on a flat map the
    /// cells are the walls and the body needs its whole radius.
    /// </summary>
    public float BodyRadius => HasGround ? 0.1f : AgentRadius;

    public IReadOnlyList<MapPoint> Points => _points;
    public float WorldWidth => Width * CellSize;
    public float WorldHeight => Height * CellSize;

    /// <summary>Rays cast since creation, for cost accounting.</summary>
    public long RaysCast;

    /// <param name="ground">Height of the walkable surface in each cell, or null for a flat map at height 0.</param>
    /// <param name="links">
    /// Per cell, a bit for each of the eight neighbours that can be walked to
    /// (bit order: +x, -x, +y, -y, then the diagonals +x+y, +x-y, -x+y, -x-y).
    /// Null means any two free neighbours connect. Needed where free cells
    /// are separated by a ledge or a wall thinner than a cell.
    /// </param>
    public GridMap(string id, int width, int height, float cellSize, byte[] blocked, List<MapPoint> points,
        Vector2 origin = default, float[]? ground = null, byte[]? links = null, TriangleMesh? mesh = null, NavMap? nav = null)
    {
        if (blocked.Length != width * height || (ground is not null && ground.Length != blocked.Length)
            || (links is not null && links.Length != blocked.Length))
            throw new ArgumentException("cell arrays do not match the map size");
        Id = id;
        Width = width;
        Height = height;
        CellSize = cellSize;
        Origin = origin;
        Mesh = mesh;
        Nav = nav;
        _blocked = blocked;
        _points = points;
        _ground = ground;
        _links = links;
    }

    public bool IsBlockedCell(int cx, int cy) =>
        (uint)cx >= (uint)Width || (uint)cy >= (uint)Height || _blocked[cy * Width + cx] != 0;

    public int CellX(float x) => (int)MathF.Floor((x - Origin.X) / CellSize);

    public int CellY(float y) => (int)MathF.Floor((y - Origin.Y) / CellSize);

    public bool IsBlocked(Vector2 p) => IsBlockedCell(CellX(p.X), CellY(p.Y));

    public Vector2 CellCenter(int cx, int cy) => new(Origin.X + (cx + 0.5f) * CellSize, Origin.Y + (cy + 0.5f) * CellSize);

    /// <summary>Height of the ground at a position: its cell's, or the nearest free cell's. 0 on a flat map.</summary>
    public float GroundAt(Vector2 p)
    {
        if (_ground is null || !NearestFree(p, out int cx, out int cy))
            return 0;
        return _ground[cy * Width + cx];
    }

    /// <summary>Whether two points can see each other, in three dimensions where the map has the level's collision.</summary>
    public bool LineOfSight(Vector3 a, Vector3 b) =>
        Mesh is not null ? Mesh.LineOfSight(a, b) : LineOfSight(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y));

    /// <summary>
    /// Where a body ends up walking from one point toward another a short
    /// step away: it slides along walls and stops at edges it cannot cross.
    /// </summary>
    public Vector2 Move(Vector2 from, Vector2 to)
    {
        float radius = BodyRadius;
        Vector2 at = from;
        Vector2 tryX = new(to.X, at.Y);
        if (CanCross(at, tryX, radius))
            at = tryX;
        Vector2 tryY = new(at.X, to.Y);
        if (CanCross(at, tryY, radius))
            at = tryY;
        return at;
    }

    private bool CanCross(Vector2 from, Vector2 to, float radius)
    {
        if (Collides(to.X, to.Y, radius))
            return false;
        int ax = CellX(from.X), ay = CellY(from.Y), bx = CellX(to.X), by = CellY(to.Y);
        if (ax == bx && ay == by)
            return true;
        if (Math.Abs(bx - ax) + Math.Abs(by - ay) != 1)
            return false; // more than one cell in a step
        return CanStep(ax, ay, bx > ax ? 0 : bx < ax ? 1 : by > ay ? 2 : 3);
    }

    public Vector2 CellCenter(int index) => CellCenter(index % Width, index / Width);

    public IEnumerable<MapPoint> PointsOf(MapPointKind kind) => _points.Where(p => p.Kind == kind);

    /// <summary>Whether a circle of the agent radius at this position overlaps a wall.</summary>
    public bool Collides(float x, float y, float radius = AgentRadius)
    {
        int x0 = CellX(x - radius), x1 = CellX(x + radius);
        int y0 = CellY(y - radius), y1 = CellY(y + radius);
        for (int cy = y0; cy <= y1; cy++)
        {
            for (int cx = x0; cx <= x1; cx++)
            {
                if (!IsBlockedCell(cx, cy))
                    continue;
                float nx = Math.Clamp(x, Origin.X + cx * CellSize, Origin.X + (cx + 1) * CellSize);
                float ny = Math.Clamp(y, Origin.Y + cy * CellSize, Origin.Y + (cy + 1) * CellSize);
                float dx = x - nx, dy = y - ny;
                if (dx * dx + dy * dy < radius * radius)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Distance along a ray on the ground plane to the first thing that
    /// cannot be walked through (a wall, or an edge between cells that do not
    /// connect), or <paramref name="maxDistance"/> if none is closer.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public float Raycast(Vector2 origin, Vector2 direction, float maxDistance)
    {
        RaysCast++;
        float ox = (origin.X - Origin.X) / CellSize, oy = (origin.Y - Origin.Y) / CellSize;
        int cx = (int)MathF.Floor(ox), cy = (int)MathF.Floor(oy);
        if (IsBlockedCell(cx, cy))
            return 0;
        int stepX = direction.X > 0 ? 1 : -1, stepY = direction.Y > 0 ? 1 : -1;
        float tDeltaX = direction.X != 0 ? MathF.Abs(1 / direction.X) : float.PositiveInfinity;
        float tDeltaY = direction.Y != 0 ? MathF.Abs(1 / direction.Y) : float.PositiveInfinity;
        float tMaxX = direction.X > 0 ? (cx + 1 - ox) / direction.X
            : direction.X < 0 ? (cx - ox) / direction.X : float.PositiveInfinity;
        float tMaxY = direction.Y > 0 ? (cy + 1 - oy) / direction.Y
            : direction.Y < 0 ? (cy - oy) / direction.Y : float.PositiveInfinity;
        float maxT = maxDistance / CellSize;
        while (true)
        {
            float t;
            if (tMaxX < tMaxY)
            {
                t = tMaxX;
                if (t > maxT)
                    return maxDistance;
                if (_links is not null && (_links[cy * Width + cx] & (stepX > 0 ? 1 : 2)) == 0)
                    return t * CellSize;
                cx += stepX;
                tMaxX += tDeltaX;
            }
            else
            {
                t = tMaxY;
                if (t > maxT || float.IsPositiveInfinity(t))
                    return maxDistance;
                if (_links is not null && (_links[cy * Width + cx] & (stepY > 0 ? 4 : 8)) == 0)
                    return t * CellSize;
                cy += stepY;
                tMaxY += tDeltaY;
            }
            if (IsBlockedCell(cx, cy))
                return t * CellSize;
        }
    }

    public bool LineOfSight(Vector2 a, Vector2 b)
    {
        Vector2 d = b - a;
        float length = d.Length();
        if (length < 1e-4f)
            return true;
        return Raycast(a, d / length, length) >= length - 1e-3f;
    }

    /// <summary>Whether an agent-sized body can walk straight from a to b.</summary>
    public bool ClearPath(Vector2 a, Vector2 b)
    {
        Vector2 d = b - a;
        float length = d.Length();
        if (length < 1e-4f)
            return true;
        Vector2 dir = d / length;
        Vector2 side = new Vector2(-dir.Y, dir.X) * (BodyRadius + 0.05f);
        return Raycast(a, dir, length) >= length - 1e-3f
            && Raycast(a + side, dir, length) >= length - 1e-3f
            && Raycast(a - side, dir, length) >= length - 1e-3f;
    }

    /// <summary>The free cell at or nearest to a position, searching a few cells out.</summary>
    public bool NearestFree(Vector2 p, out int cx, out int cy)
    {
        int px = CellX(p.X), py = CellY(p.Y);
        for (int r = 0; r <= 4; r++)
        {
            for (int y = py - r; y <= py + r; y++)
            {
                for (int x = px - r; x <= px + r; x++)
                {
                    if (Math.Max(Math.Abs(x - px), Math.Abs(y - py)) != r || IsBlockedCell(x, y))
                        continue;
                    cx = x;
                    cy = y;
                    return true;
                }
            }
        }
        cx = cy = 0;
        return false;
    }

    /// <summary>Whether a body in a free cell can walk to its neighbour in direction <paramref name="d"/> (0 to 7).</summary>
    public bool CanStep(int cx, int cy, int d)
    {
        int nx = cx + Dx[d], ny = cy + Dy[d];
        if (IsBlockedCell(nx, ny))
            return false;
        if (_links is not null)
            return (_links[cy * Width + cx] & (1 << d)) != 0;
        // No cutting corners on diagonals.
        return d < 4 || (!IsBlockedCell(nx, cy) && !IsBlockedCell(cx, ny));
    }

    /// <summary>
    /// A* from a to b, then shortened so each leg is a straight walk. Returns
    /// false if no path was found within the node budget.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public bool FindPath(Vector2 from, Vector2 to, List<Vector2> path, int maxNodes = 40000)
    {
        path.Clear();
        if (!NearestFree(from, out int sx, out int sy) || !NearestFree(to, out int tx, out int ty))
            return false;
        _g ??= new int[Width * Height];
        _stamp ??= new int[Width * Height];
        _parent ??= new int[Width * Height];
        _generation++;
        _open.Clear();
        int start = sy * Width + sx, goal = ty * Width + tx;
        _g[start] = 0;
        _stamp[start] = _generation;
        _parent[start] = -1;
        _open.Enqueue(start, Octile(sx, sy, tx, ty));
        bool found = false;
        int expanded = 0;
        while (_open.TryDequeue(out int current, out int priority))
        {
            if (current == goal)
            {
                found = true;
                break;
            }
            int cx = current % Width, cy = current / Width;
            int g = _g[current];
            if (priority > g + Octile(cx, cy, tx, ty))
                continue; // a better route to this cell was queued later
            if (++expanded > maxNodes)
                break;
            for (int d = 0; d < 8; d++)
            {
                if (!CanStep(cx, cy, d))
                    continue;
                int nx = cx + Dx[d], ny = cy + Dy[d];
                int n = ny * Width + nx;
                int ng = g + (d < 4 ? 10 : 14);
                if (_stamp[n] == _generation && _g[n] <= ng)
                    continue;
                _stamp[n] = _generation;
                _g[n] = ng;
                _parent[n] = current;
                _open.Enqueue(n, ng + Octile(nx, ny, tx, ty));
            }
        }
        if (!found)
            return false;

        _cells.Clear();
        for (int c = goal; c != -1; c = _parent[c])
            _cells.Add(c);
        _cells.Reverse();

        Vector2 anchor = from;
        int i = 0;
        while (i < _cells.Count - 1)
        {
            int j = Math.Min(_cells.Count - 1, i + 32);
            for (; j > i + 1; j--)
            {
                if (ClearPath(anchor, CellCenter(_cells[j])))
                    break;
            }
            anchor = CellCenter(_cells[j]);
            path.Add(anchor);
            i = j;
        }
        if (path.Count == 0)
            path.Add(CellCenter(goal));
        if (!IsBlocked(to) && ClearPath(path[^1], to))
            path[^1] = to;
        return true;
    }

    private static int Octile(int x, int y, int tx, int ty)
    {
        int dx = Math.Abs(x - tx), dy = Math.Abs(y - ty);
        return 10 * (dx + dy) - 6 * Math.Min(dx, dy);
    }

    /// <summary>Walking distance from every cell to a target, shared by everyone heading there.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public FlowField BuildFlowField(Vector2 target)
    {
        int[] dist = new int[Width * Height];
        Array.Fill(dist, int.MaxValue);
        if (!NearestFree(target, out int tx, out int ty))
            return new GridFlowField(this, dist, target);
        PriorityQueue<int, int> queue = new();
        int goal = ty * Width + tx;
        dist[goal] = 0;
        queue.Enqueue(goal, 0);
        while (queue.TryDequeue(out int current, out int priority))
        {
            if (priority > dist[current])
                continue;
            int cx = current % Width, cy = current / Width;
            for (int d = 0; d < 8; d++)
            {
                // The field runs backward from the target, so the step that matters is the neighbour's into this cell.
                int nx = cx + Dx[d], ny = cy + Dy[d];
                if (IsBlockedCell(nx, ny) || !CanStep(nx, ny, Opposite[d]))
                    continue;
                int n = ny * Width + nx;
                int nd = priority + (d < 4 ? 10 : 14);
                if (nd >= dist[n])
                    continue;
                dist[n] = nd;
                queue.Enqueue(n, nd);
            }
        }
        return new GridFlowField(this, dist, target);
    }

    /// <summary>
    /// Walking distance in metres from a start to every cell (infinity where
    /// it cannot be walked to), the opposite way round from a flow field: off a
    /// one-way drop counts, back up it does not. For offline use; not tuned for mid-match calls.
    /// </summary>
    public float[] DistancesFrom(Vector2 start)
    {
        float[] metres = new float[Width * Height];
        Array.Fill(metres, float.PositiveInfinity);
        if (!NearestFree(start, out int sx, out int sy))
            return metres;
        int[] dist = new int[Width * Height];
        Array.Fill(dist, int.MaxValue);
        PriorityQueue<int, int> queue = new();
        int first = sy * Width + sx;
        dist[first] = 0;
        queue.Enqueue(first, 0);
        while (queue.TryDequeue(out int current, out int priority))
        {
            if (priority > dist[current])
                continue;
            int cx = current % Width, cy = current / Width;
            for (int d = 0; d < 8; d++)
            {
                if (!CanStep(cx, cy, d))
                    continue;
                int n = (cy + Dy[d]) * Width + cx + Dx[d];
                int nd = priority + (d < 4 ? 10 : 14);
                if (nd >= dist[n])
                    continue;
                dist[n] = nd;
                queue.Enqueue(n, nd);
            }
        }
        for (int i = 0; i < dist.Length; i++)
        {
            if (dist[i] != int.MaxValue)
                metres[i] = dist[i] * 0.1f * CellSize;
        }
        return metres;
    }

    internal int BestNeighbour(int[] dist, int cx, int cy)
    {
        int best = -1, bestDist = dist[cy * Width + cx];
        for (int d = 0; d < 8; d++)
        {
            if (!CanStep(cx, cy, d))
                continue;
            int n = (cy + Dy[d]) * Width + cx + Dx[d];
            if (dist[n] < bestDist)
            {
                bestDist = dist[n];
                best = n;
            }
        }
        return best;
    }

    public byte[] Serialize()
    {
        using MemoryStream stream = new();
        using (BinaryWriter w = new(stream, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            // Version 3 added the navigation mesh, version 4 each point's floor height. Without a mesh the
            // package stays version 2, byte for byte, so its hash does not change.
            int version = Nav is null ? 2 : 4;
            w.Write(version);
            w.Write(Id);
            w.Write(Width);
            w.Write(Height);
            w.Write(CellSize);
            w.Write(Origin.X);
            w.Write(Origin.Y);
            w.Write(_blocked);
            w.Write(_ground is not null);
            if (_ground is not null)
                w.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_ground.AsSpan()));
            w.Write(_links is not null);
            if (_links is not null)
                w.Write(_links);
            w.Write(Mesh is not null);
            Mesh?.Write(w);
            if (Nav is not null)
                Nav.Write(w);
            w.Write(_points.Count);
            foreach (MapPoint p in _points)
            {
                w.Write((byte)p.Kind);
                w.Write(p.Team);
                w.Write(p.Id);
                w.Write(p.Position.X);
                w.Write(p.Position.Y);
                w.Write(p.Radius);
                if (version >= 4)
                    w.Write(p.Height);
            }
        }
        return stream.ToArray();
    }

    // ---- Questions with heights (M11 stage 2): answered on the navigation mesh when the package has one, else on the grid. ----

    /// <summary>Whether the brain's movement questions go to the navigation mesh.</summary>
    public bool OnMesh => Nav is not null;

    /// <summary>A point on the grid's ground, with its height.</summary>
    public Vector3 OnGround(Vector2 p) => new(p, HasGround ? GroundAt(p) : 0);

    /// <summary>Distances to a target from everywhere: over the mesh by floor, or over the grid.</summary>
    public FlowField BuildFlowField(Vector3 target) =>
        Nav is { } nav ? nav.BuildFlowField(target, OnGround) : BuildFlowField(new Vector2(target.X, target.Y));

    /// <summary>A route between two points as its waypoints, with heights; false when there is none.</summary>
    public bool FindPath(Vector3 from, Vector3 to, List<Vector3> path, int maxNodes = 40000)
    {
        if (Nav is { } nav)
            return nav.FindPath(from, to, path);
        List<Vector2> flat = [];
        path.Clear();
        if (!FindPath(new Vector2(from.X, from.Y), new Vector2(to.X, to.Y), flat, maxNodes))
            return false;
        foreach (Vector2 p in flat)
            path.Add(OnGround(p));
        return true;
    }

    /// <summary>Whether a body can walk straight from one point to the other (on the mesh: along the floor it stands on).</summary>
    public bool ClearPath(Vector3 a, Vector3 b) =>
        Nav is { } nav ? nav.StraightWalk(a, b) : ClearPath(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y));

    /// <summary>Whether the way between two points is open at ground level (the grid's line of sight; on the mesh, a straight walk).</summary>
    public bool OpenLine(Vector3 a, Vector3 b) =>
        Nav is { } nav ? nav.StraightWalk(a, b) : LineOfSight(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y));

    /// <summary>Whether a body can stand at a point.</summary>
    public bool Standable(Vector3 feet) =>
        Nav is { } nav ? nav.Standable(feet) : !Collides(feet.X, feet.Y, BodyRadius);

    /// <summary>Whether there is walkable floor at a point (on the grid: the cell is free, whatever room a body has).</summary>
    public bool OnFloor(Vector3 feet) =>
        Nav is { } nav ? nav.Standable(feet) : !IsBlocked(new Vector2(feet.X, feet.Y));

    /// <summary>The floor nearest a point (on the grid: the nearest free cell, a few cells out).</summary>
    public bool SnapToFloor(Vector3 point, out Vector3 floor)
    {
        if (Nav is { } nav)
            return nav.Snap(point, 4f, out floor);
        floor = default;
        if (!NearestFree(new Vector2(point.X, point.Y), out int cx, out int cy))
            return false;
        floor = OnGround(CellCenter(cx, cy));
        return true;
    }

    /// <summary>The height of the floor at a point nearest a given height (on the grid there is only the one).</summary>
    public float FloorHeight(Vector2 p, float near)
    {
        if (Nav is { } nav && nav.Floor(new Vector3(p, near), out _, out Vector3 floor, across: 1f, below: 3f, above: 2f))
            return floor.Z;
        return HasGround ? GroundAt(p) : 0;
    }

    /// <summary>
    /// The same map with a navigation mesh added (the map tool builds the mesh
    /// after the grid), and optionally its points replaced (the map tool's
    /// roam and camp points found on the mesh, with their floors).
    /// </summary>
    public GridMap WithNav(NavMap nav, List<MapPoint>? points = null) => new(Id, Width, Height, CellSize, _blocked, points ?? _points, Origin, _ground, _links, Mesh, nav);

    /// <summary>Where a point is, with its floor's height: its own (version 4) or the grid's ground under it.</summary>
    public Vector3 PointAt(MapPoint p) => float.IsNaN(p.Height) ? OnGround(p.Position) : new Vector3(p.Position, p.Height);

    public static GridMap Deserialize(byte[] data)
    {
        using BinaryReader r = new(new MemoryStream(data), Encoding.UTF8);
        int version = r.ReadUInt32() == Magic ? r.ReadInt32() : -1;
        if (version is not (2 or 3 or 4))
            throw new InvalidDataException("not a grid map package of a version this build reads (2 to 4)");
        string id = r.ReadString();
        int width = r.ReadInt32(), height = r.ReadInt32();
        float cellSize = r.ReadSingle();
        Vector2 origin = new(r.ReadSingle(), r.ReadSingle());
        byte[] blocked = r.ReadBytes(width * height);
        float[]? ground = null;
        if (r.ReadBoolean())
        {
            ground = new float[width * height];
            r.BaseStream.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(ground.AsSpan()));
        }
        byte[]? links = r.ReadBoolean() ? r.ReadBytes(width * height) : null;
        TriangleMesh? mesh = r.ReadBoolean() ? TriangleMesh.Read(r) : null;
        NavMap? nav = version >= 3 ? NavMap.Read(r) : null;
        int count = r.ReadInt32();
        List<MapPoint> points = new(count);
        for (int i = 0; i < count; i++)
        {
            MapPointKind kind = (MapPointKind)r.ReadByte();
            byte team = r.ReadByte();
            int pid = r.ReadInt32();
            float x = r.ReadSingle(), y = r.ReadSingle(), radius = r.ReadSingle();
            float z = version >= 4 ? r.ReadSingle() : float.NaN;
            points.Add(new MapPoint(kind, team, pid, new Vector2(x, y), radius, z));
        }
        return new GridMap(id, width, height, cellSize, blocked, points, origin, ground, links, mesh, nav);
    }

    private string? _hash;

    public string Hash() => _hash ??= Convert.ToHexStringLower(SHA256.HashData(Serialize()));
}

/// <summary>
/// Distances to one target over the whole map, and the way to walk there
/// from anywhere: on the grid (<see cref="GridFlowField"/>), or over the
/// navigation mesh, where a position's height says which floor it is on.
/// </summary>
public abstract class FlowField
{
    public abstract Vector2 Target { get; }
    public abstract float TargetHeight { get; }

    /// <summary>Walking distance to the target in metres from feet at this point, or infinity if it cannot be reached.</summary>
    public abstract float DistanceFrom(Vector3 position);

    /// <summary>A point on the ground plane to walk straight at, from feet at this point.</summary>
    public abstract Vector2 SteerPoint(Vector3 position, int lookahead = 10);

    /// <summary>The same, from a point on the grid's ground (no height known).</summary>
    public float DistanceFrom(Vector2 position) => DistanceFrom(Ground(position));

    public Vector2 SteerPoint(Vector2 position, int lookahead = 10) => SteerPoint(Ground(position), lookahead);

    protected abstract Vector3 Ground(Vector2 position);
}

/// <summary>Distances to one target over the grid's cells.</summary>
public sealed class GridFlowField(GridMap map, int[] distance, Vector2 target) : FlowField
{
    public override Vector2 Target { get; } = target;
    public override float TargetHeight => map.HasGround ? map.GroundAt(Target) : 0;
    protected override Vector3 Ground(Vector2 position) => new(position, 0);

    public override float DistanceFrom(Vector3 position) => DistanceOnGrid(new Vector2(position.X, position.Y));

    public override Vector2 SteerPoint(Vector3 position, int lookahead = 10) => SteerOnGrid(new Vector2(position.X, position.Y), lookahead);

    private float DistanceOnGrid(Vector2 position)
    {
        if (!map.NearestFree(position, out int cx, out int cy))
            return float.PositiveInfinity;
        int d = distance[cy * map.Width + cx];
        return d == int.MaxValue ? float.PositiveInfinity : d * 0.1f * map.CellSize;
    }

    /// <summary>
    /// A point to walk straight at: as far down the route as can be reached
    /// in a straight line, up to <paramref name="lookahead"/> cells.
    /// </summary>
    private Vector2 SteerOnGrid(Vector2 position, int lookahead)
    {
        if (!map.NearestFree(position, out int cx, out int cy))
            return position;
        int cell = cy * map.Width + cx;
        if (distance[cell] == int.MaxValue)
            return position;
        Vector2 best = map.CellCenter(cell);
        bool first = true;
        for (int step = 0; step < lookahead; step++)
        {
            int next = map.BestNeighbour(distance, cell % map.Width, cell / map.Width);
            if (next < 0)
            {
                if (map.ClearPath(position, Target))
                    best = Target;
                break;
            }
            cell = next;
            Vector2 centre = map.CellCenter(cell);
            // The first step is always taken so a body pressed against a corner still moves.
            if (!first && !map.ClearPath(position, centre))
                break;
            best = centre;
            first = false;
        }
        return best;
    }
}

public interface IMapStore
{
    GridMap? Find(string id, string hash);
}

public sealed class MemoryMapStore : IMapStore
{
    private readonly Dictionary<string, GridMap> _maps = [];

    public void Add(GridMap map) => _maps[map.Id + ":" + map.Hash()] = map;

    public GridMap? Find(string id, string hash) => _maps.GetValueOrDefault(id + ":" + hash);
}

/// <summary>Map packages as files named <c>id-hash.bbmap</c> in one folder.</summary>
public sealed class DirectoryMapStore(string directory) : IMapStore
{
    public static string FileName(string id, string hash) => $"{id}-{hash}.bbmap";

    public string Save(GridMap map)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, FileName(map.Id, map.Hash()));
        File.WriteAllBytes(path, map.Serialize());
        return path;
    }

    public GridMap? Find(string id, string hash)
    {
        if (id.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || hash.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;
        string path = Path.Combine(directory, FileName(id, hash));
        if (!File.Exists(path))
            return null;
        GridMap map = GridMap.Deserialize(File.ReadAllBytes(path));
        return map.Hash() == hash ? map : null;
    }
}
