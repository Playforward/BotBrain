using System.Diagnostics;
using System.Numerics;
using BotBrain.Maps;
using DotRecast.Recast.Geom;
using DotRecast.Recast.Toolset;
using DotRecast.Recast.Toolset.Builder;
using DotRecast.Core.Numerics;
using DotRecast.Detour;

namespace BotBrain.MapTool;

/// <summary>
/// Builds a level's navigation mesh with Recast (DotRecast): every floor a
/// body fits on, then one-way links down every ledge it can drop off (by the
/// grid's own rule, <see cref="AddDrops"/>), then the mesh again with those
/// links in it. See docs/M11-LAYERED-MAP.md.
/// </summary>
internal static class NavMeshBuild
{
    /// <summary>
    /// The body: 0.25 m radius (Recast narrows the floor by this much from
    /// every wall, rounded up to cells; at 0.4 m MAG's one-metre doorways
    /// closed and a defenders' spawn on kennicott was walled in), 1.8 m tall,
    /// 0.45 m steps as the grid allows, drops of up to 4 m as the grid
    /// allows, 0.25 m cells.
    /// </summary>
    public static readonly NavMap.Body Default = new(Radius: 0.25f, Height: 1.8f, Climb: 0.45f, MaxDrop: 4f, CellSize: 0.25f);

    public static NavMap Build(TriangleMesh mesh, NavMap.Body body, out double seconds)
    {
        Stopwatch clock = Stopwatch.StartNew();
        ReadOnlySpan<Vector3> vertices = mesh.Vertices;
        ReadOnlySpan<int> triangles = mesh.Triangles;
        float[] v = new float[vertices.Length * 3];
        for (int i = 0; i < vertices.Length; i++)
        {
            v[i * 3] = vertices[i].X;
            v[i * 3 + 1] = vertices[i].Z;
            v[i * 3 + 2] = vertices[i].Y;
        }
        // Level collision is not wound consistently, and Recast drops faces that point down:
        // turn every triangle up. (The swap to y-up reverses the winding, hence the < 0.)
        int[] f = new int[triangles.Length];
        for (int i = 0; i < triangles.Length; i += 3)
        {
            Vector3 a = vertices[triangles[i]], b = vertices[triangles[i + 1]], c = vertices[triangles[i + 2]];
            bool keep = Vector3.Cross(b - a, c - a).Z < 0;
            f[i] = triangles[i];
            f[i + 1] = keep ? triangles[i + 1] : triangles[i + 2];
            f[i + 2] = keep ? triangles[i + 2] : triangles[i + 1];
        }
        RcSampleInputGeomProvider geometry = new(v, f);
        RcNavMeshBuildSettings settings = new()
        {
            cellSize = body.CellSize,
            cellHeight = 0.2f,
            agentHeight = body.Height,
            agentRadius = body.Radius,
            agentMaxClimb = body.Climb,
            agentMaxSlope = 50f, // as the grid's MinLevelness 0.64
            minRegionSize = 8,
            mergedRegionSize = 20,
            edgeMaxLen = 12f,
            edgeMaxError = 1.3f,
            vertsPerPoly = 6,
            detailSampleDist = 6f,
            detailSampleMaxError = 1f,
            tiled = true,
            tileSize = 64,
            keepInterResults = false,
            buildAll = true,
        };
        NavMeshBuildResult first = new TileNavMeshBuilder().Build(geometry, settings);
        Drops = AddDrops(first.NavMesh, mesh, geometry, body);
        NavMeshBuildResult second = new TileNavMeshBuilder().Build(geometry, settings);
        seconds = clock.Elapsed.TotalSeconds;
        return new NavMap(second.NavMesh, body);
    }

    /// <summary>How many drop links the last build added (for the map tool's report).</summary>
    public static int Drops;

    /// <summary>
    /// One-way drop links by the grid's rule (MeshMapBuilder): along every
    /// outer edge of the floors, a floor 0.6 m to <see cref="NavMap.Body.MaxDrop"/>
    /// below, 0.6 to 2 m out from the edge, with the way across clear at
    /// knee, waist and chest height and the way down clear. One link per
    /// metre of edge. Recast's own jump-link tool looked for the landing
    /// closer in and missed ledges the grid crossed (a 2.2 m one on
    /// cosmodrome sent defenders 370 m round instead of 100 m straight).
    /// </summary>
    private static int AddDrops(DtNavMesh nav, TriangleMesh mesh, RcSampleInputGeomProvider geometry, NavMap.Body body)
    {
        DtNavMeshQuery query = new(nav);
        DtQueryDefaultFilter filter = new();
        HashSet<long> taken = [];
        // Out to 2 m: a body stepping off at a run carries about 3 m forward on a 3 m fall, and a
        // wall top between an edge and the floor below (cosmodrome's spawn side) puts the floor 1.5 m out.
        float[] reach = [0.6f, 0.9f, 1.2f, 1.6f, 2.0f];
        float[] probes = [0.5f, 1.0f, 1.5f];
        int added = 0;
        for (int t = 0; t < nav.GetMaxTiles(); t++)
        {
            if (nav.GetTile(t)?.data is not { header: not null } data)
                continue;
            for (int p = 0; p < data.header.polyCount; p++)
            {
                DtPoly poly = data.polys[p];
                if (poly.GetPolyType() != DtPolyTypes.DT_POLYTYPE_GROUND)
                    continue;
                Vector3 Vert(int k) => NavMap.FromRecast(new RcVec3f(data.verts[poly.verts[k] * 3], data.verts[poly.verts[k] * 3 + 1], data.verts[poly.verts[k] * 3 + 2]));
                Vector3 centre = default;
                for (int k = 0; k < poly.vertCount; k++)
                    centre += Vert(k) / poly.vertCount;
                for (int k = 0; k < poly.vertCount; k++)
                {
                    if (poly.neis[k] != 0)
                        continue; // a neighbour (in this tile or the next): not an outer edge
                    Vector3 a = Vert(k), b = Vert((k + 1) % poly.vertCount);
                    Vector2 along = new(b.X - a.X, b.Y - a.Y);
                    float length = along.Length();
                    if (length < 0.3f)
                        continue;
                    along /= length;
                    Vector2 outward = new(along.Y, -along.X);
                    Vector2 mid = new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                    if (Vector2.Dot(outward, mid - new Vector2(centre.X, centre.Y)) < 0)
                        outward = -outward;
                    for (float s = Math.Min(0.25f, length / 2); s <= length - 0.1f; s += 0.5f)
                    {
                        Vector3 edge = Vector3.Lerp(a, b, s / length) - new Vector3(outward * 0.05f, 0);
                        foreach (float d in reach)
                        {
                            Vector2 out2 = new Vector2(edge.X, edge.Y) + outward * d;
                            // A floor below, under the point out there.
                            query.FindNearestPoly(NavMap.ToRecast(new Vector3(out2, edge.Z - (0.6f + body.MaxDrop) / 2)), new RcVec3f(0.15f, (body.MaxDrop - 0.6f) / 2 + 0.05f, 0.15f),
                                filter, out long landRef, out RcVec3f landR, out bool over);
                            if (landRef == 0 || !over)
                                continue;
                            Vector3 land = NavMap.FromRecast(landR);
                            float drop = edge.Z - land.Z;
                            if (drop < 0.6f || drop > body.MaxDrop)
                                continue;
                            bool clear = true;
                            foreach (float h in probes)
                                clear &= mesh.LineOfSight(edge + new Vector3(0, 0, h), new Vector3(out2, edge.Z + h));
                            clear &= mesh.LineOfSight(new Vector3(out2, edge.Z + 0.5f), new Vector3(out2, land.Z + 0.3f));
                            if (!clear)
                                break; // something in the way out there: no further try on this spot either
                            long key = ((long)MathF.Floor(edge.X) << 40) ^ ((long)MathF.Floor(edge.Y) << 20) ^ (long)MathF.Floor(edge.Z * 2);
                            if (taken.Add(key))
                            {
                                geometry.AddOffMeshConnection(NavMap.ToRecast(edge), NavMap.ToRecast(land), 0.3f, false, 5, 8);
                                added++;
                            }
                            break;
                        }
                    }
                }
            }
        }
        return added;
    }
}
