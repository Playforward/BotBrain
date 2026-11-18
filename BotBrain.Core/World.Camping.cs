using System.Numerics;
using BotBrain.Maps;
using BotBrain.Protocol;

namespace BotBrain.Core;

/// <summary>
/// Campers: players who find a spot with a long view over where the enemy
/// comes from, and wait there. Spots are the map's vantage points (the map
/// tool finds them); a map without any falls back to its interest points.
/// </summary>
public sealed partial class World
{
    private MapPoint[]? _campPoints;
    /// <summary>The map has no vantage points, so the interest points stand in (and are the roam points, index for index).</summary>
    private bool _campOnInterest;
    /// <summary>For each team, the direction to watch from each spot.</summary>
    private readonly Dictionary<byte, float[]> _campWatch = [];
    /// <summary>Which agent (by slot) holds each spot for each team; -1 for nobody.</summary>
    private readonly Dictionary<byte, int[]> _campHeld = [];

    private bool EnsureCampPoints()
    {
        if (_campPoints is null)
        {
            _campPoints = [.. _map.PointsOf(MapPointKind.Vantage)];
            if (_campPoints.Length == 0)
            {
                _campPoints = [.. _map.PointsOf(MapPointKind.Interest)];
                _campOnInterest = EnsureRoamFields();
            }
        }
        return _campPoints.Length > 0;
    }

    private int[] HeldBy(byte team)
    {
        if (!_campHeld.TryGetValue(team, out int[]? held))
        {
            held = new int[_campPoints!.Length];
            Array.Fill(held, -1);
            _campHeld[team] = held;
        }
        return held;
    }

    /// <summary>
    /// The way to face from each spot, for one team: of 24 directions, the one
    /// that sees most open ground 10 to 50 m out, leaning toward the enemy's
    /// side. Worked out once per team, about 400 rays.
    /// </summary>
    private float[] WatchFor(byte team)
    {
        if (_campWatch.TryGetValue(team, out float[]? watch))
            return watch;
        Vector2 enemy = default;
        int enemies = 0;
        foreach (MapPoint p in _map.PointsOf(MapPointKind.Spawn))
        {
            if (p.Team != team && _hostile[team * 256 + p.Team])
            {
                enemy += p.Position;
                enemies++;
            }
        }
        watch = new float[_campPoints!.Length];
        ReadOnlySpan<float> reach = [10f, 20f, 35f, 50f];
        for (int i = 0; i < watch.Length; i++)
        {
            Vector2 at = _campPoints[i].Position;
            float ground = _map.OnMesh ? _map.PointAt(_campPoints[i]).Z : _map.HasGround ? _map.GroundAt(at) : 0;
            Vector3 eye = new(at, ground + EyeHeight);
            Vector2 toEnemy = enemies > 0 && Vector2.DistanceSquared(enemy / enemies, at) > 1f ? Vector2.Normalize(enemy / enemies - at) : Vector2.Zero;
            float best = -1, bestYaw = 0;
            for (int k = 0; k < 24; k++)
            {
                float yaw = k * MathF.PI / 12;
                Vector2 dir = new(MathF.Cos(yaw), MathF.Sin(yaw));
                int open = 0;
                foreach (float r in reach)
                {
                    Vector2 p = at + dir * r;
                    float z;
                    if (_map.OnMesh)
                    {
                        // Any floor out there, the street below a balcony included.
                        if (!_map.Nav!.Floor(new Vector3(p, ground), out _, out Vector3 floor, across: 1f, below: 12f, above: 3f))
                            continue;
                        z = floor.Z + ChestHeight;
                    }
                    else
                    {
                        if (_map.IsBlocked(p))
                            continue;
                        z = _map.HasGround ? _map.GroundAt(p) + ChestHeight : eye.Z;
                    }
                    if (_map.LineOfSight(eye, new Vector3(p, z)))
                        open++;
                }
                float score = open * (1.2f + Vector2.Dot(dir, toEnemy));
                if (score > best)
                {
                    best = score;
                    bestYaw = yaw;
                }
            }
            watch[i] = bestYaw;
        }
        _campWatch[team] = watch;
        return watch;
    }

    /// <summary>
    /// Maybe settle somewhere to camp: by the persona's <c>Camper</c> chance,
    /// not straight after leaving a spot, at a spot no teammate holds, near
    /// the bot's front line and not too far away.
    /// </summary>
    private void ConsiderCamping(Agent a, Vector2 position)
    {
        // Attackers mostly keep moving; a few still settle somewhere to cover the push.
        float camper = a.Role == Role.Attacker ? a.P.Camper * 0.4f : a.P.Camper;
        if (a.CampSpot >= 0 || _now < a.CampCooldownUntil || a.Rng.NextFloat() >= camper || !EnsureCampPoints())
            return;
        if (_zones.Length > 0 && (uint)a.Zone >= (uint)_zones.Length)
            return; // not yet told which objective to play
        int[] held = HeldBy(a.Team);
        Vector2 home = default, away = default;
        int homes = 0, aways = 0;
        foreach (MapPoint p in _map.PointsOf(MapPointKind.Spawn))
        {
            if (p.Team == a.Team)
            {
                home += p.Position;
                homes++;
            }
            else if (_hostile[a.Team * 256 + p.Team])
            {
                away += p.Position;
                aways++;
            }
        }
        Vector2 axis = homes > 0 && aways > 0 ? away / aways - home / homes : Vector2.Zero;
        Span<float> weight = _campPoints!.Length <= 128 ? stackalloc float[_campPoints.Length] : new float[_campPoints.Length];
        float total = 0;
        for (int i = 0; i < _campPoints.Length; i++)
        {
            weight[i] = 0;
            if (held[i] >= 0 || (i == a.CampAvoid && _now < a.CampAvoidUntil))
                continue;
            Vector2 at = _campPoints[i].Position;
            float front = axis.LengthSquared() > 1f ? Vector2.Dot(at - home / homes, axis) / axis.LengthSquared() : 0.5f;
            // Campers hang a little behind the line they would otherwise roam to.
            float f = (front - (a.P.FrontLine - 0.15f)) / 0.25f;
            float distance = Vector2.Distance(position, at);
            weight[i] = MathF.Exp(-f * f) / (1f + distance / 80f);
            // Interest points were not chosen for their view: at least keep to the ones on the way between the sides.
            if (_campOnInterest)
                weight[i] *= LayoutFor(a.Team).Route[i];
            // With objectives to play, a camper watches over the one it was sent to.
            if (_zones.Length > 0 && (uint)a.Zone < (uint)_zones.Length)
            {
                float z = Vector2.Distance(at, _zones[a.Zone].Position) / (_zones[a.Zone].Radius + 25f);
                weight[i] *= MathF.Exp(-z * z);
            }
            else if (PlaysSite(a))
            {
                float z = Vector2.Distance(at, _sites[a.Site].Stand) / 35f;
                weight[i] *= MathF.Exp(-z * z);
            }
            total += weight[i];
        }
        if (total <= 0)
            return;
        float pick = a.Rng.NextFloat() * total;
        int spot = _campPoints.Length - 1;
        for (int i = 0; i < _campPoints.Length; i++)
        {
            pick -= weight[i];
            if (pick <= 0 && weight[i] > 0)
            {
                spot = i;
                break;
            }
        }
        held[spot] = a.Slot;
        a.CampSpot = spot;
        a.AtCamp = false;
        a.CampStaySet = false;
        a.CampUntil = _now + 60; // to get there; the stay is set on arrival
        a.CampWatch = WatchFor(a.Team)[spot];
        a.HuntFlow = null;
        a.Path.Clear();
        a.HasSteer = false;
        a.StuckRun = 0;
        a.NeedsDecision = true;
        Stats.Camps++;
        RecordCamp(a);
    }

    /// <summary>Leaves the spot: free for a teammate, and no new camp for a while.</summary>
    internal void EndCamp(Agent a, bool burned = false)
    {
        if (a.CampSpot < 0)
            return;
        int[] held = HeldBy(a.Team);
        if (held[a.CampSpot] == a.Slot)
            held[a.CampSpot] = -1;
        if (burned)
        {
            // Shot from somewhere it could not see: this spot is known now.
            a.CampAvoid = a.CampSpot;
            a.CampAvoidUntil = _now + 90;
        }
        a.CampSpot = -1;
        a.AtCamp = false;
        // Goal.Camp always has a spot. Decide can return without picking a
        // goal (no path searches left this tick), so do not leave Camp behind.
        if (a.Goal == Goal.Camp)
        {
            a.Goal = Goal.None;
            a.HasSteer = false;
        }
        a.CampCooldownUntil = _now + a.Rng.Range(15f, 40f);
        a.NeedsDecision = true;
    }

    /// <summary>How much an agent wants to stay camping, compared like the other goals in Decide.</summary>
    private float CampScore(Agent a)
    {
        if (a.CampSpot < 0)
            return 0;
        if (_now >= a.CampUntil)
        {
            EndCamp(a);
            return 0;
        }
        return (0.55f + 0.35f * a.P.Camper) * (a.Goal == Goal.Camp ? 1.15f : 1f);
    }

    /// <summary>Whether the agent is sitting at its spot right now (and should fight from it, not advance).</summary>
    private bool Camping(Agent a, Vector2 position) =>
        a.CampSpot >= 0 && a.AtCamp && Vector2.DistanceSquared(position, _campPoints![a.CampSpot].Position) < 9f;

    /// <summary>
    /// The camp skill: get to the spot, then stand there facing the watch
    /// direction, looking about within it and now and then over a shoulder.
    /// </summary>
    private void Camp(Agent a, ref EntityRecord me, Vector2 position)
    {
        if ((uint)a.CampSpot >= (uint)(_campPoints?.Length ?? 0))
        {
            a.Goal = Goal.None;
            a.NeedsDecision = true;
            LookAround(a, ref me, position);
            return;
        }
        Vector2 spot = _campPoints![a.CampSpot].Position;
        // Pulled away by a fight: walk back to the spot.
        if (a.AtCamp && Vector2.DistanceSquared(position, spot) > 3f * 3f)
        {
            a.AtCamp = false;
            a.HasSteer = false;
        }
        if (!a.AtCamp)
        {
            // On the mesh a spot may be upstairs: standing under it is not being at it.
            bool level = !_map.OnMesh || MathF.Abs(me.Z - _map.PointAt(_campPoints[a.CampSpot]).Z) < 1.5f;
            if (level && Vector2.DistanceSquared(position, spot) < 1.5f * 1.5f)
            {
                a.AtCamp = true;
                if (!a.CampStaySet)
                {
                    a.CampStaySet = true;
                    a.CampUntil = _now + a.Rng.Range(40f, 120f) * (0.5f + a.P.Camper);
                }
                a.CampLookUntil = 0;
            }
            else
            {
                Travel(a, ref me, position);
                return;
            }
        }
        if (_now >= a.CampLookUntil)
        {
            a.CampLook = a.Rng.NextFloat() < 0.05f
                ? a.CampWatch + MathF.PI + a.Rng.Range(-0.5f, 0.5f)
                : a.CampWatch + Math.Clamp(a.Rng.Gaussian() * 0.45f * a.P.ScanHabit, -0.9f, 0.9f);
            a.CampLookUntil = _now + a.Rng.Range(1.2f, 3.5f);
        }
        a.Intent.HasLook = true;
        a.Intent.LookPoint = new Vector3(position + new Vector2(MathF.Cos(a.CampLook), MathF.Sin(a.CampLook)) * 25f, me.Z + EyeHeight - 0.1f);
        // Drifted off the spot (pushed, or after a fight): step back onto it.
        if (Vector2.DistanceSquared(position, spot) > 0.6f * 0.6f)
        {
            a.Intent.Move = Vector2.Normalize(spot - position);
            a.Intent.Speed = 0.5f;
        }
    }
}
