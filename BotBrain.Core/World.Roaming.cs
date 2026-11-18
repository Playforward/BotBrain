using System.Diagnostics;
using System.Numerics;
using BotBrain.Maps;
using BotBrain.Protocol;

namespace BotBrain.Core;

/// <summary>
/// Where agents go when nothing is in front of them: roaming toward the fight
/// rather than to the edges of the map, and hunting a place an enemy is
/// believed to be (the one who killed them, gunfire heard in the distance),
/// head on or round the side.
/// </summary>
public sealed partial class World
{
    /// <summary>A place a team knows an enemy was: seen there, shot from there, or killed a teammate from there.</summary>
    private struct Contact
    {
        public byte Team;
        public Vector2 Position;
        public double At;
    }

    /// <summary>For one team, how each roam point lies between its spawns and the enemy's.</summary>
    private sealed class RoamLayout(int count)
    {
        /// <summary>1 on the shortest way between the two sides, falling toward 0 the further out of the way.</summary>
        public readonly float[] Route = new float[count];
        /// <summary>0 at the team's own spawns, 1 at the enemy's.</summary>
        public readonly float[] Front = new float[count];
    }

    private const int MaxContacts = 48;
    /// <summary>Contacts this close together are one place.</summary>
    private const float ContactMerge = 15f;
    /// <summary>How far a contact's pull reaches around it, metres.</summary>
    private const float ContactReach = 35f;
    /// <summary>How fast a contact goes cold (seconds to fall to a third), and when it is dropped.</summary>
    private const float ContactFade = 25f, ContactLife = 60f;
    /// <summary>How far out of the way a point may be before it stops being worth roaming to: 0.25 = a quarter longer than the direct way.</summary>
    private const float RouteSpread = 0.25f;
    private const float FrontSpread = 0.22f;
    /// <summary>Gunfire carries further than the hearing that makes a bot turn round (HearRange): far enough to go and look.</summary>
    private const float FarHearRange = 160f;
    /// <summary>Hunts further than this go by the roam points' route fields first, then a route search for the last stretch.</summary>
    private const float HuntSearchRange = 50f;

    private readonly List<Contact> _contacts = [];
    private readonly Dictionary<byte, RoamLayout> _roamLayouts = [];
    private bool _contactsChanged;

    /// <summary>The map's interest points, each with a route field, built the first time anyone needs them.</summary>
    private bool EnsureRoamFields()
    {
        if (_roamFlow is not null)
            return _roamFlow.Length > 0;
        long start = Stopwatch.GetTimestamp();
        _roamPoints = [.. _map.PointsOf(MapPointKind.Interest)];
        _roamFlow = new FlowField[_roamPoints.Length];
        for (int i = 0; i < _roamPoints.Length; i++)
            _roamFlow[i] = _map.BuildFlowField(_map.PointAt(_roamPoints[i]));
        Stats.FlowFieldMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return _roamFlow.Length > 0;
    }

    /// <summary>
    /// Works out, once per team, how far each roam point is out of the way
    /// between its spawns and the enemy's, and how far forward it is. Uses the
    /// walking distances the roam points' route fields already hold, so it
    /// costs no new searches. A map without spawns of both sides gets every
    /// point weighted alike.
    /// </summary>
    private RoamLayout LayoutFor(byte team)
    {
        if (_roamLayouts.TryGetValue(team, out RoamLayout? layout))
            return layout;
        int n = _roamPoints.Length;
        layout = new RoamLayout(n);
        List<Vector3> own = [], enemy = [];
        foreach (MapPoint p in _map.PointsOf(MapPointKind.Spawn))
        {
            if (p.Team == team)
                own.Add(_map.PointAt(p));
            else if (_hostile[team * 256 + p.Team])
                enemy.Add(_map.PointAt(p));
        }
        float[] toOwn = new float[n], toEnemy = new float[n];
        float shortest = float.PositiveInfinity;
        for (int i = 0; i < n; i++)
        {
            toOwn[i] = MeanDistance(_roamFlow![i], own);
            toEnemy[i] = MeanDistance(_roamFlow[i], enemy);
            shortest = MathF.Min(shortest, toOwn[i] + toEnemy[i]);
        }
        for (int i = 0; i < n; i++)
        {
            float sum = toOwn[i] + toEnemy[i];
            if (float.IsInfinity(shortest))
            {
                layout.Route[i] = 1;
                layout.Front[i] = 0.5f;
            }
            else if (float.IsInfinity(sum))
            {
                // Cannot be walked to from one side or the other.
                layout.Route[i] = 0.02f;
                layout.Front[i] = 0.5f;
            }
            else
            {
                float detour = (sum / shortest - 1f) / RouteSpread;
                layout.Route[i] = MathF.Max(0.02f, MathF.Exp(-detour * detour));
                layout.Front[i] = toOwn[i] / MathF.Max(1f, sum);
            }
        }
        _roamLayouts[team] = layout;
        return layout;
    }

    private static float MeanDistance(FlowField field, List<Vector3> from)
    {
        float sum = 0;
        int count = 0;
        foreach (Vector3 p in from)
        {
            float d = field.DistanceFrom(p);
            if (float.IsInfinity(d))
                continue;
            sum += d;
            count++;
        }
        return count == 0 ? float.PositiveInfinity : sum / count;
    }

    /// <summary>How much the team's recent contacts pull toward a place: 0 for nothing known, up to 2.</summary>
    private float HeatAt(byte team, Vector2 position)
    {
        float heat = 0;
        foreach (Contact c in _contacts)
        {
            if (c.Team != team)
                continue;
            float age = (float)(_now - c.At);
            float d2 = Vector2.DistanceSquared(c.Position, position);
            heat += MathF.Exp(-age / ContactFade - d2 / (ContactReach * ContactReach));
        }
        return MathF.Min(heat, 2f);
    }

    /// <summary>How much a team member who likes the given front line wants to roam to a point.</summary>
    private float RoamScore(byte team, int point, float frontLine)
    {
        RoamLayout layout = LayoutFor(team);
        float f = (layout.Front[point] - frontLine) / FrontSpread;
        return layout.Route[point] * (0.15f + 0.85f * MathF.Exp(-f * f)) * (1f + 4f * HeatAt(team, _roamPoints[point].Position));
    }

    /// <summary>
    /// A roam point drawn by weight: on the way between the two sides, near the
    /// chosen front line, and above all near where the team last met the enemy.
    /// Points it cannot walk to, is standing at, or has just left are skipped.
    /// </summary>
    private int ChooseRoamPoint(byte team, float frontLine, ref Rng rng, Vector3? from, int exclude)
    {
        int n = _roamPoints.Length;
        Span<float> weight = n <= 128 ? stackalloc float[n] : new float[n];
        float total = 0;
        for (int i = 0; i < n; i++)
        {
            weight[i] = 0;
            if (i == exclude)
                continue;
            if (from is { } position)
            {
                float distance = _roamFlow![i].DistanceFrom(position);
                if (float.IsInfinity(distance) || distance <= 12f)
                    continue;
            }
            weight[i] = RoamScore(team, i, frontLine);
            total += weight[i];
        }
        if (total <= 0)
            return -1;
        float pick = rng.NextFloat() * total;
        for (int i = 0; i < n; i++)
        {
            pick -= weight[i];
            if (pick <= 0 && weight[i] > 0)
                return i;
        }
        return n - 1;
    }

    /// <summary>
    /// Chooses where a roaming agent heads next: its squad's point, unless it
    /// is going its own way or has just been there, and never somewhere it
    /// cannot walk to or is already standing.
    /// </summary>
    private void PickRoamTarget(Agent a, Vector2 position, bool own)
    {
        Squad squad = _squads[a.Squad];
        int previous = a.RoamTarget, pick = -1;
        Vector3 feet = new(position, _entities[a.EntityIndex].Z);
        if (!own && squad.RoamTarget >= 0 && squad.RoamTarget != previous && a.Rng.NextFloat() < a.P.Obedience)
        {
            float distance = _roamFlow![squad.RoamTarget].DistanceFrom(feet);
            if (!float.IsInfinity(distance) && distance > 12f)
                pick = squad.RoamTarget;
        }
        if (pick < 0)
            pick = ChooseRoamPoint(a.Team, a.P.FrontLine, ref a.Rng, feet, previous);
        if (pick < 0)
            pick = a.Rng.NextInt(_roamPoints.Length);
        a.RoamTarget = pick;
        a.Flow = _roamFlow![pick];
        a.HasSteer = false;
    }

    /// <summary>Whether the squad's point has gone cold while the team is fighting somewhere else.</summary>
    private bool FightMovedAway(Squad s)
    {
        if (_now < s.RoamSince + 8)
            return false;
        float here = HeatAt(s.Team, _roamPoints[s.RoamTarget].Position), best = 0;
        for (int i = 0; i < _roamPoints.Length; i++)
            best = MathF.Max(best, HeatAt(s.Team, _roamPoints[i].Position));
        return best >= 0.6f && here < 0.2f * best;
    }

    /// <summary>Remembers, for a team, that an enemy was at a place just now.</summary>
    private void AddContact(byte team, Vector2 position)
    {
        int oldest = -1;
        for (int i = 0; i < _contacts.Count; i++)
        {
            Contact c = _contacts[i];
            if (c.Team == team && Vector2.DistanceSquared(c.Position, position) < ContactMerge * ContactMerge)
            {
                // Same place: keep it fresh, drifting toward the newest report.
                _contacts[i] = c with { Position = Vector2.Lerp(c.Position, position, 0.3f), At = _now };
                return;
            }
            if (oldest < 0 || c.At < _contacts[oldest].At)
                oldest = i;
        }
        Contact added = new() { Team = team, Position = position, At = _now };
        if (_contacts.Count < MaxContacts)
            _contacts.Add(added);
        else
            _contacts[oldest] = added;
        _contactsChanged = true;
    }

    /// <summary>Once a second: forget cold contacts and record what each team knows.</summary>
    private void UpdateContacts()
    {
        int removed = _contacts.RemoveAll(c => _now - c.At > ContactLife);
        if (removed > 0)
            _contactsChanged = true;
        if (_contactsChanged)
            RecordContacts();
        _contactsChanged = false;
    }

    /// <summary>Starts a hunt: going to where an enemy is believed to be, round the side if this player flanks.</summary>
    private void StartHunt(Agent a, HuntReason reason, Vector2 target, float targetZ, float seconds)
    {
        if (a.EntityIndex < 0 || !EnsureRoamFields())
            return;
        Vector2 position = PositionOf(a.EntityIndex);
        if (!_map.SnapToFloor(new Vector3(target, targetZ), out Vector3 floor))
            return;
        target = Flat(floor);
        a.Hunt = reason;
        a.HuntPoint = target;
        a.HuntZ = floor.Z;
        a.HuntStarted = _now;
        a.HuntUntil = _now + seconds;
        a.HuntFlow = null;
        a.Path.Clear();
        a.HasSteer = false;
        a.StuckRun = 0;
        a.HasHuntVia = a.Rng.NextFloat() < a.P.Flank && FlankPoint(a, position, target, out a.HuntVia, out a.HuntViaZ);
        a.NeedsDecision = true;
        Stats.Hunts[(int)reason]++;
        if (a.HasHuntVia)
            Stats.Flanks++;
        RecordHunt(a);
    }

    /// <summary>
    /// A point off to one side of the line from here to the target and short
    /// of it, so the last stretch comes in across the enemy's view rather than
    /// down it. Fails where neither side has walkable ground.
    /// </summary>
    private bool FlankPoint(Agent a, Vector2 from, Vector2 target, out Vector2 via, out float viaZ)
    {
        via = default;
        viaZ = 0;
        Vector2 to = target - from;
        float distance = to.Length();
        if (distance < 25f)
            return false;
        Vector2 forward = to / distance, side = new(-forward.Y, forward.X);
        float reach = Math.Clamp(distance * 0.45f, 20f, 45f);
        float first = a.Rng.NextFloat() < 0.5f ? -1 : 1;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            float s = attempt == 0 ? first : -first;
            Vector2 candidate = target - forward * reach * 0.4f + side * s * reach;
            if (!_map.SnapToFloor(new Vector3(candidate, a.HuntZ), out Vector3 floor))
                continue;
            Vector2 cell = Flat(floor);
            // Snapping must not have dragged it back onto the direct line or far from where it was meant to be.
            if (Vector2.DistanceSquared(cell, candidate) > 10f * 10f)
                continue;
            via = cell;
            viaZ = floor.Z;
            return true;
        }
        return false;
    }

    /// <summary>How much an agent wants to carry on its hunt, compared like the other goals in Decide.</summary>
    private float HuntScore(Agent a)
    {
        if (a.Hunt == HuntReason.None)
            return 0;
        if (_now >= a.HuntUntil)
        {
            a.Hunt = HuntReason.None;
            return 0;
        }
        float left = (float)((a.HuntUntil - _now) / Math.Max(1, a.HuntUntil - a.HuntStarted));
        float want = a.Hunt == HuntReason.Revenge ? 0.6f + 0.4f * a.P.Grudge : 0.5f + 0.4f * a.P.Rush;
        return want * (0.5f + 0.5f * left) * (a.Goal == Goal.Hunt ? 1.15f : 1f);
    }

    /// <summary>
    /// The next point to walk at on a hunt. Far legs follow the route field of
    /// the roam point nearest the leg's end; the last stretch is a route
    /// search. Returns false when the hunt is over (arrived, or no way there).
    /// </summary>
    private bool HuntSteer(Agent a, Vector2 position)
    {
        Vector2 leg = a.HasHuntVia ? a.HuntVia : a.HuntPoint;
        float legZ = a.HasHuntVia ? a.HuntViaZ : a.HuntZ;
        if (Vector2.DistanceSquared(position, leg) < 9f)
        {
            if (!a.HasHuntVia)
            {
                // Got there and nobody is here. Let it go.
                a.Hunt = HuntReason.None;
                return false;
            }
            a.HasHuntVia = false;
            a.HuntFlow = null;
            a.Path.Clear();
            leg = a.HuntPoint;
            legZ = a.HuntZ;
        }

        if (RouteTo(a, position, leg, legZ))
            return true;
        a.Hunt = HuntReason.None;
        return false;
    }

    /// <summary>
    /// Sets <c>a.Steer</c> toward a place anywhere on the map. Far away it
    /// follows the route field of the roam point nearest the place
    /// (<c>a.HuntFlow</c>); within <see cref="HuntSearchRange"/>, or at that
    /// roam point, it searches a route (<c>a.Path</c>). False when there is no
    /// way there. The caller clears <c>a.Path</c> and <c>a.HuntFlow</c> when
    /// the place changes.
    /// </summary>
    private bool RouteTo(Agent a, Vector2 position, Vector2 leg, float legZ)
    {
        Vector3 feet = Feet(a);
        if (a.Path.Count > 0 && a.PathGoal == leg)
        {
            if (FollowPath(a, position))
                return true;
            a.Path.Clear(); // knocked off the route: search again from here
        }

        bool near = Vector2.DistanceSquared(position, leg) < HuntSearchRange * HuntSearchRange;
        if (a.HuntFlow is null && !near && EnsureRoamFields())
        {
            // The roam point nearest the leg's end that can be walked to from here.
            int best = -1;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < _roamPoints.Length; i++)
            {
                float d = Vector2.DistanceSquared(_roamPoints[i].Position, leg);
                if (d < bestDistance && !float.IsInfinity(_roamFlow![i].DistanceFrom(feet)))
                {
                    bestDistance = d;
                    best = i;
                }
            }
            if (best >= 0)
                a.HuntFlow = _roamFlow![best];
        }
        bool atFlowEnd = a.HuntFlow is not null && Vector2.DistanceSquared(position, a.HuntFlow.Target) < 36f;
        if ((near || a.HuntFlow is null || atFlowEnd) && _pathBudget > 0)
        {
            _pathBudget--;
            Stats.PathRequests++;
            if (_map.FindPath(feet, new Vector3(leg, legZ), a.Path, _options.MaxPathNodes))
            {
                a.PathIndex = 0;
                a.PathGoal = leg;
                a.Steer = Flat(a.Path[0]);
                return true;
            }
            Stats.PathFailures++;
            a.Path.Clear();
            if (near || atFlowEnd || a.HuntFlow is null)
                return false;
        }
        if (a.HuntFlow is null)
            return true; // waiting for a route search; keep the last steering point
        a.Steer = a.HuntFlow.SteerPoint(feet);
        return true;
    }

    /// <summary>
    /// When an agent dies: the team learns where the killer was, and if the
    /// agent bears grudges it will go back there after it respawns. The killer
    /// is whoever hurt it last, within a few seconds; the game's kill screen
    /// shows players who killed them, so knowing where is fair.
    /// </summary>
    private void OnAgentDied(Agent a)
    {
        a.RevengePending = false;
        // Killed at its spot: the enemy knows it now.
        EndCamp(a, burned: a.AtCamp);
        if (a.LastAttacker == 0 || _now - a.LastAttackedAt > 4 || !_entityIndex.TryGetValue(a.LastAttacker, out int killer))
            return;
        if (!_hostile[a.Team * 256 + _entities[killer].Team])
            return;
        Vector2 at = PositionOf(killer);
        AddContact(a.Team, at);
        if (a.Rng.NextFloat() >= a.P.Grudge)
            return;
        a.RevengePending = true;
        a.RevengePoint = at + new Vector2(a.Rng.Gaussian(), a.Rng.Gaussian()) * 4f;
        a.RevengeZ = _entities[killer].Z;
    }

    private void OnAgentSpawned(Agent a)
    {
        if (!a.RevengePending)
        {
            ConsiderCamping(a, PositionOf(a.EntityIndex));
            return;
        }
        a.RevengePending = false;
        StartHunt(a, HuntReason.Revenge, a.RevengePoint, a.RevengeZ, a.Rng.Range(60f, 100f));
    }

    /// <summary>
    /// Gunfire carries a long way. Players who go to it do: more often the
    /// more they like to rush, and only if they are not busy with something
    /// closer to hand. Each listener hears at most every two seconds.
    /// </summary>
    private void HearFarGunfire(byte shooterTeam, Vector2 source, float sourceZ)
    {
        int count = QueryNear(source, FarHearRange);
        for (int i = 0; i < count; i++)
        {
            int agentIndex = _agentOfEntity[_near[i]];
            if (agentIndex < 0)
                continue;
            Agent a = _agents[agentIndex];
            if (!a.Alive || !_hostile[a.Team * 256 + shooterTeam] || _now < a.NextHearAt)
                continue;
            a.NextHearAt = _now + 2;
            if (a.Goal is Goal.Engage or Goal.Investigate || a.Hunt != HuntReason.None)
                continue;
            Vector2 position = PositionOf(a.EntityIndex);
            float distance = Vector2.Distance(position, source);
            if (distance < 25f)
                continue; // close enough that the near-hearing alert handles it
            // Heard, roughly: the further away, the worse the guess of where.
            Vector2 guess = source + new Vector2(a.Rng.Gaussian(), a.Rng.Gaussian()) * (3f + 0.08f * distance);
            AddContact(a.Team, guess);
            if (a.Rng.NextFloat() < a.P.Rush * 0.5f)
                StartHunt(a, HuntReason.Gunfire, guess, sourceZ, a.Rng.Range(30f, 50f));
        }
    }
}
