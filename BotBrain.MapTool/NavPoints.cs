using System.Numerics;
using BotBrain.Maps;

namespace BotBrain.MapTool;

/// <summary>
/// The map tool's points, found on the navigation mesh (M11 stage 4): every
/// floor counts, so a balcony or an upper storey can be a roam point or a
/// camp spot. The same rules as the grid's (MeshMapBuilder: RouteBand,
/// Spread, Vantages), on the mesh's places (its polygons), with each point's
/// floor height kept (package version 4).
/// </summary>
internal static class NavPoints
{
    /// <summary>Places to choose from: one per cube of this size (floors 2.5 m apart are told apart).</summary>
    private const float Sample = 2f, FloorStep = 2.5f;

    public static List<MapPoint> Find(GridMap grid, NavMap nav, TriangleMesh mesh, IReadOnlyList<MapSeed> seeds, MeshMapOptions options, MeshMapReport report)
    {
        List<MapPoint> points = [];
        // Spawns and the level's named places, now with the height of the floor under each.
        foreach (MapPoint p in grid.Points)
        {
            if (p.Kind == MapPointKind.Spawn)
            {
                MapSeed? seed = seeds.FirstOrDefault(s => s.Id == p.Id && s.Team == p.Team);
                Vector3 at = seed is { } s ? s.Position : grid.OnGround(p.Position);
                float z = nav.Floor(at, out _, out Vector3 floor, across: 3f, below: 1.5f, above: 1f) ? floor.Z : at.Z;
                points.Add(p with { Height = z });
            }
            else if (p.Kind == MapPointKind.Interest && p.Id >= 1000)
                points.Add(p with { Height = grid.OnGround(p.Position).Z }); // named in the level
        }

        List<int> candidates = Thin(nav, Enumerable.Range(0, nav.Places).Where(i => !nav.IsDrop(i)), Sample);
        List<int> band = RouteBand(nav, candidates, seeds, options, out List<float> balance);
        points.AddRange(Spread(nav, candidates, band, balance, options, report));
        points.AddRange(Vantages(nav, mesh, band, options, report));
        return points;
    }

    private static long Key(Vector3 p, float size) =>
        ((long)MathF.Floor(p.X / size) * 73856093L) ^ ((long)MathF.Floor(p.Y / size) * 19349663L) ^ ((long)MathF.Floor(p.Z / FloorStep) * 83492791L);

    /// <summary>The places, keeping one per cube of the given size.</summary>
    private static List<int> Thin(NavMap nav, IEnumerable<int> places, float size)
    {
        HashSet<long> taken = [];
        List<int> kept = [];
        foreach (int i in places)
            if (taken.Add(Key(nav.PlaceCentre(i), size)))
                kept.Add(i);
        return kept;
    }

    /// <summary>
    /// The places on the ways between the two sides' spawns, with how
    /// unevenly each lies between them: walking out from each side, as on
    /// the grid (spawns may be platforms that can be left but not climbed
    /// back onto).
    /// </summary>
    private static List<int> RouteBand(NavMap nav, List<int> candidates, IReadOnlyList<MapSeed> seeds, MeshMapOptions options, out List<float> balance)
    {
        List<int> onRoutes = [];
        balance = [];
        if (!SideAnchors(nav, seeds, out Vector3 home, out Vector3 away))
            return onRoutes;
        float[] fromHome = nav.DistancesFrom(home), fromAway = nav.DistancesFrom(away);
        float direct = float.PositiveInfinity;
        foreach (int c in candidates)
            direct = MathF.Min(direct, fromHome[c] + fromAway[c]);
        if (float.IsInfinity(direct) || direct <= 0)
            return onRoutes;
        foreach (int c in candidates)
        {
            float a = fromHome[c], b = fromAway[c];
            if (float.IsInfinity(a) || float.IsInfinity(b) || a + b > direct * (1 + options.InterestRouteSlack)
                || a < options.InterestSpawnClearance || b < options.InterestSpawnClearance)
                continue;
            onRoutes.Add(c);
            balance.Add(MathF.Abs(a - b));
        }
        return onRoutes;
    }

    /// <summary>Roam points as far from each other as the routes allow (in three dimensions: floors count as apart), starting in the middle.</summary>
    private static List<MapPoint> Spread(NavMap nav, List<int> all, List<int> band, List<float> balance, MeshMapOptions options, MeshMapReport report)
    {
        int count = options.InterestPoints;
        List<MapPoint> result = [];
        List<int> free = all;
        int pick = all.Count / 2;
        report.InterestOnRoutes = false;
        if (band.Count >= count * 8)
        {
            free = band;
            pick = balance.IndexOf(balance.Min());
            report.InterestOnRoutes = true;
        }
        if (free.Count == 0)
            return result;
        report.InterestArea = free.Count * Sample * Sample;
        float[] nearest = new float[free.Count];
        Array.Fill(nearest, float.MaxValue);
        for (int n = 0; n < count; n++)
        {
            Vector3 at = nav.PlaceCentre(free[pick]);
            result.Add(new MapPoint(MapPointKind.Interest, 255, n, new Vector2(at.X, at.Y), 3f, at.Z));
            float far = -1;
            for (int i = 0; i < free.Count; i++)
            {
                Vector3 d = nav.PlaceCentre(free[i]) - at;
                d.Z *= 3f; // a floor above is further away than its height says
                nearest[i] = MathF.Min(nearest[i], d.LengthSquared());
                if (nearest[i] > far)
                {
                    far = nearest[i];
                    pick = i;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Camp spots: on the routes, on any floor, seeing as much of the routes
    /// 10 to 70 m away as possible (eye 1.6 m up, chest 1.2 m up, through the
    /// level's triangles), better with something solid close by. At least
    /// 25 m apart.
    /// </summary>
    private static List<MapPoint> Vantages(NavMap nav, TriangleMesh mesh, List<int> band, MeshMapOptions options, MeshMapReport report)
    {
        List<MapPoint> result = [];
        report.Vantages = 0;
        if (options.VantagePoints <= 0 || band.Count == 0)
            return result;
        List<int> targets = Thin(nav, band, 6f), candidates = Thin(nav, band, 3f);
        if (targets.Count > 600)
            targets = [.. targets.Where((_, k) => k % (targets.Count / 600 + 1) == 0)];
        Vector3[] chests = [.. targets.Select(t => nav.PlaceCentre(t) + new Vector3(0, 0, 1.2f))];
        float[] score = new float[candidates.Count];
        int[] seen = new int[candidates.Count];
        Parallel.For(0, candidates.Count, c =>
        {
            Vector3 at = nav.PlaceCentre(candidates[c]);
            Vector3 eye = at + new Vector3(0, 0, 1.6f);
            int count = 0;
            foreach (Vector3 chest in chests)
            {
                float d2 = Vector2.DistanceSquared(new Vector2(at.X, at.Y), new Vector2(chest.X, chest.Y));
                if (d2 < 10f * 10f || d2 > 70f * 70f)
                    continue;
                if (mesh.LineOfSight(eye, chest))
                    count++;
            }
            int cover = 0;
            for (int k = 0; k < 8; k++)
            {
                float angle = k * MathF.PI / 4;
                if (!nav.Standable(at + new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0) * 1.2f))
                    cover++;
            }
            seen[c] = count;
            score[c] = cover >= 6 ? 0 : count * (1f + 0.12f * Math.Min(cover, 3));
        });
        int[] order = [.. Enumerable.Range(0, candidates.Count).OrderByDescending(c => score[c])];
        foreach (int c in order)
        {
            if (result.Count >= options.VantagePoints || score[c] <= 0)
                break;
            Vector3 at = nav.PlaceCentre(candidates[c]);
            if (result.Any(p => Vector2.DistanceSquared(p.Position, new Vector2(at.X, at.Y)) < 25f * 25f))
                continue;
            result.Add(new MapPoint(MapPointKind.Vantage, 255, result.Count, new Vector2(at.X, at.Y), 2f, at.Z));
            float share = seen[c] / (float)Math.Max(1, chests.Length);
            report.VantageBestShare = result.Count == 1 ? share : report.VantageBestShare;
            report.VantageWorstShare = share;
        }
        report.Vantages = result.Count;
        return result;
    }

    /// <summary>For each side (the first seed's team, and the rest), the spawn on a floor nearest that side's middle.</summary>
    private static bool SideAnchors(NavMap nav, IReadOnlyList<MapSeed> seeds, out Vector3 home, out Vector3 away)
    {
        home = away = default;
        if (seeds.Count == 0)
            return false;
        byte first = seeds[0].Team;
        for (int side = 0; side < 2; side++)
        {
            List<Vector3> mine = [.. seeds.Where(s => (s.Team == first) == (side == 0)).Select(s => s.Position)];
            if (mine.Count == 0)
                return false;
            Vector3 middle = mine.Aggregate(Vector3.Zero, (sum, p) => sum + p) / mine.Count;
            Vector3? best = null;
            foreach (Vector3 p in mine.OrderBy(p => Vector3.DistanceSquared(p, middle)))
            {
                if (nav.PlaceAt(p) >= 0 || nav.Floor(p, out _, out _, across: 3f, below: 1.5f, above: 1f))
                {
                    best = p;
                    break;
                }
            }
            if (best is null)
                return false;
            if (side == 0)
                home = best.Value;
            else
                away = best.Value;
        }
        return true;
    }
}
