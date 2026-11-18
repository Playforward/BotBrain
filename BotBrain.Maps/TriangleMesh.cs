using System.Numerics;

namespace BotBrain.Maps;

/// <summary>
/// A level's collision as triangles, with a grid of buckets over the ground
/// plane so a ray only tests the triangles near it. Z is up.
/// Not thread-safe only in its ray counter.
/// </summary>
public sealed class TriangleMesh
{
    private const uint Magic = 0x50534242; // "BBSP"

    private readonly Vector3[] _vertices;
    private readonly int[] _triangles;
    private readonly float _bucket, _inverseBucket, _minX, _minY, _maxX, _maxY;
    private readonly int _nx, _ny;
    private readonly int[] _start, _items;

    public int VertexCount => _vertices.Length;
    public ReadOnlySpan<Vector3> Vertices => _vertices;
    /// <summary>Three vertex indices per triangle.</summary>
    public ReadOnlySpan<int> Triangles => _triangles;
    public int TriangleCount => _triangles.Length / 3;
    public Vector3 Min { get; }
    public Vector3 Max { get; }

    /// <summary>Rays cast since creation, for cost accounting.</summary>
    public long RaysCast;

    public TriangleMesh(Vector3[] vertices, int[] triangles, float bucketSize = 4f)
    {
        _vertices = vertices;
        _triangles = triangles;
        _bucket = bucketSize;
        _inverseBucket = 1f / bucketSize;
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (Vector3 v in vertices)
        {
            min = Vector3.Min(min, v);
            max = Vector3.Max(max, v);
        }
        if (vertices.Length == 0)
            min = max = default;
        Min = min;
        Max = max;
        _minX = min.X;
        _minY = min.Y;
        _maxX = max.X;
        _maxY = max.Y;
        _nx = Math.Max(1, (int)MathF.Ceiling((max.X - min.X) * _inverseBucket));
        _ny = Math.Max(1, (int)MathF.Ceiling((max.Y - min.Y) * _inverseBucket));

        // Two passes: count the buckets each triangle's bounds touch, then fill.
        _start = new int[_nx * _ny + 1];
        for (int t = 0; t < TriangleCount; t++)
        {
            Bounds(t, out int x0, out int y0, out int x1, out int y1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    _start[y * _nx + x + 1]++;
        }
        for (int b = 1; b < _start.Length; b++)
            _start[b] += _start[b - 1];
        _items = new int[_start[^1]];
        int[] cursor = (int[])_start.Clone();
        for (int t = 0; t < TriangleCount; t++)
        {
            Bounds(t, out int x0, out int y0, out int x1, out int y1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    _items[cursor[y * _nx + x]++] = t;
        }
    }

    private void Bounds(int t, out int x0, out int y0, out int x1, out int y1)
    {
        Vector3 a = _vertices[_triangles[t * 3]], b = _vertices[_triangles[t * 3 + 1]], c = _vertices[_triangles[t * 3 + 2]];
        x0 = BucketX(MathF.Min(a.X, MathF.Min(b.X, c.X)));
        x1 = BucketX(MathF.Max(a.X, MathF.Max(b.X, c.X)));
        y0 = BucketY(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)));
        y1 = BucketY(MathF.Max(a.Y, MathF.Max(b.Y, c.Y)));
    }

    private int BucketX(float x) => Math.Clamp((int)((x - _minX) * _inverseBucket), 0, _nx - 1);

    private int BucketY(float y) => Math.Clamp((int)((y - _minY) * _inverseBucket), 0, _ny - 1);

    /// <summary>Distance along a ray to the first triangle, or <paramref name="maxDistance"/> if none is closer. Triangles are two-sided.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public float Raycast(Vector3 origin, Vector3 direction, float maxDistance)
    {
        RaysCast++;
        float enter = 0, exit = maxDistance;
        if (!Clip(origin.X, direction.X, _minX, _maxX, ref enter, ref exit)
            || !Clip(origin.Y, direction.Y, _minY, _maxY, ref enter, ref exit))
            return maxDistance;

        Vector3 p = origin + direction * enter;
        int bx = BucketX(p.X), by = BucketY(p.Y);
        int stepX = direction.X > 0 ? 1 : -1, stepY = direction.Y > 0 ? 1 : -1;
        float deltaX = direction.X != 0 ? _bucket / MathF.Abs(direction.X) : float.PositiveInfinity;
        float deltaY = direction.Y != 0 ? _bucket / MathF.Abs(direction.Y) : float.PositiveInfinity;
        float nextX = direction.X > 0 ? (_minX + (bx + 1) * _bucket - origin.X) / direction.X
            : direction.X < 0 ? (_minX + bx * _bucket - origin.X) / direction.X : float.PositiveInfinity;
        float nextY = direction.Y > 0 ? (_minY + (by + 1) * _bucket - origin.Y) / direction.Y
            : direction.Y < 0 ? (_minY + by * _bucket - origin.Y) / direction.Y : float.PositiveInfinity;

        float best = maxDistance;
        while (true)
        {
            int bucket = by * _nx + bx;
            for (int k = _start[bucket]; k < _start[bucket + 1]; k++)
            {
                float t = Intersect(_items[k], origin, direction);
                if (t >= 0 && t < best)
                    best = t;
            }
            float leave = MathF.Min(nextX, nextY);
            if (best <= leave || leave >= exit)
                break;
            if (nextX < nextY)
            {
                bx += stepX;
                nextX += deltaX;
                if ((uint)bx >= (uint)_nx)
                    break;
            }
            else
            {
                by += stepY;
                nextY += deltaY;
                if ((uint)by >= (uint)_ny)
                    break;
            }
        }
        return best;
    }

    public bool LineOfSight(Vector3 a, Vector3 b)
    {
        Vector3 d = b - a;
        float length = d.Length();
        if (length < 1e-4f)
            return true;
        return Raycast(a, d / length, length) >= length - 1e-3f;
    }

    private static bool Clip(float origin, float direction, float min, float max, ref float enter, ref float exit)
    {
        if (direction == 0)
            return origin >= min && origin <= max;
        float t0 = (min - origin) / direction, t1 = (max - origin) / direction;
        if (t0 > t1)
            (t0, t1) = (t1, t0);
        enter = MathF.Max(enter, t0);
        exit = MathF.Min(exit, t1);
        return enter <= exit;
    }

    private float Intersect(int t, Vector3 origin, Vector3 direction)
    {
        Vector3 v0 = _vertices[_triangles[t * 3]];
        Vector3 e1 = _vertices[_triangles[t * 3 + 1]] - v0, e2 = _vertices[_triangles[t * 3 + 2]] - v0;
        Vector3 h = Vector3.Cross(direction, e2);
        float a = Vector3.Dot(e1, h);
        if (MathF.Abs(a) < 1e-9f)
            return -1;
        float f = 1f / a;
        Vector3 s = origin - v0;
        float u = f * Vector3.Dot(s, h);
        if (u < 0 || u > 1)
            return -1;
        Vector3 q = Vector3.Cross(s, e1);
        float v = f * Vector3.Dot(direction, q);
        if (v < 0 || u + v > 1)
            return -1;
        float distance = f * Vector3.Dot(e2, q);
        return distance > 1e-4f ? distance : -1;
    }

    /// <summary>
    /// Every surface above or below a point on the ground plane: its height
    /// and how level it is (1 = flat, 0 = vertical). Unsorted. Returns the
    /// number written.
    /// </summary>
    public int Column(float x, float y, Span<float> heights, Span<float> levelness)
    {
        if (x < _minX || x > _maxX || y < _minY || y > _maxY)
            return 0;
        int bucket = BucketY(y) * _nx + BucketX(x), count = 0;
        for (int k = _start[bucket]; k < _start[bucket + 1] && count < heights.Length; k++)
        {
            int t = _items[k];
            Vector3 a = _vertices[_triangles[t * 3]], b = _vertices[_triangles[t * 3 + 1]], c = _vertices[_triangles[t * 3 + 2]];
            float d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
            if (MathF.Abs(d) < 1e-9f)
                continue;
            float w1 = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / d;
            float w2 = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / d;
            float w3 = 1 - w1 - w2;
            if (w1 < 0 || w2 < 0 || w3 < 0)
                continue;
            Vector3 normal = Vector3.Cross(b - a, c - a);
            float length = normal.Length();
            heights[count] = w1 * a.Z + w2 * b.Z + w3 * c.Z;
            levelness[count] = length > 0 ? MathF.Abs(normal.Z) / length : 0;
            count++;
        }
        return count;
    }

    public void Write(BinaryWriter w)
    {
        w.Write(Magic);
        w.Write(1);
        w.Write(_vertices.Length);
        w.Write(TriangleCount);
        foreach (Vector3 v in _vertices)
        {
            w.Write(v.X);
            w.Write(v.Y);
            w.Write(v.Z);
        }
        foreach (int i in _triangles)
            w.Write(i);
    }

    public static TriangleMesh Read(BinaryReader r)
    {
        if (r.ReadUInt32() != Magic || r.ReadInt32() != 1)
            throw new InvalidDataException("not a triangle mesh");
        int vertexCount = r.ReadInt32(), triangleCount = r.ReadInt32();
        Vector3[] vertices = new Vector3[vertexCount];
        for (int i = 0; i < vertexCount; i++)
            vertices[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        int[] triangles = new int[triangleCount * 3];
        for (int i = 0; i < triangles.Length; i++)
        {
            triangles[i] = r.ReadInt32();
            if ((uint)triangles[i] >= (uint)vertexCount)
                throw new InvalidDataException("triangle index out of range");
        }
        return new TriangleMesh(vertices, triangles);
    }

    public static TriangleMesh Load(string path)
    {
        using BinaryReader reader = new(File.OpenRead(path));
        return Read(reader);
    }
}
