using System.Diagnostics;
using System.Numerics;
using BotBrain.Maps;
using BotBrain.Protocol;

namespace BotBrain.Core;

public sealed partial class World
{
    private struct Zone
    {
        public int Id;
        public Vector2 Position;
        public float Height;
        public float Radius;
        public int Owner;
    }

    private sealed class Squad(byte team, int index)
    {
        public readonly byte Team = team;
        public readonly int Index = index;
        public readonly List<Agent> Members = [];
        public int Zone = -1;
        public int RoamTarget = -1;
        public double RoamSince, RoamUntil;
    }

    private Zone[] _zones = [];
    private FlowField[] _zoneFlow = [];
    private MapPoint[] _roamPoints = [];
    private FlowField[]? _roamFlow;
    private Rng _squadRng = new(77);
    private readonly List<Squad> _squads = [];

    /// <summary>The match's objectives: hold_zone and interact are understood (World.Objectives.cs).</summary>
    public void SetObjectives(ObjectivesMessage message)
    {
        long start = Stopwatch.GetTimestamp();
        RecordJson(BlockObjectives, message);
        List<Zone> zones = [];
        foreach (ObjectiveInfo info in message.Objectives)
        {
            if (info.Primitive == "hold_zone")
                zones.Add(new Zone { Id = info.Id, Position = new Vector2(info.X, info.Y), Height = info.Z, Radius = info.Radius, Owner = info.OwnerTeam });
        }
        _zones = [.. zones];
        SetSites(message);
        _zoneFlow = new FlowField[_zones.Length];
        for (int i = 0; i < _zones.Length; i++)
            _zoneFlow[i] = _map.BuildFlowField(new Vector3(_zones[i].Position, _zones[i].Height));
        // One route search now, so its compile and scratch buffers are paid for before the match.
        if (_zones.Length > 1)
            _map.FindPath(_zones[0].Position, _zones[^1].Position, [], _options.MaxPathNodes);
        Stats.FlowFieldMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        foreach (Squad squad in _squads)
            squad.Zone = -1;
        // Roaming and hunting both route through the interest points: get those ready before the match, not during it.
        EnsureRoamFields();
        // Likewise which way each camp spot faces, for every team already seated.
        if (EnsureCampPoints())
            foreach (byte team in _agents.Select(x => x.Team).Distinct())
                WatchFor(team);
    }

    private void OnObjective(int id, int owner, float progress, ObjectiveFlags flags)
    {
        OnSiteEvent(id, owner, progress, flags);
        for (int i = 0; i < _zones.Length; i++)
        {
            if (_zones[i].Id == id)
                _zones[i].Owner = owner;
        }
    }

    private void AssignSquad(Agent agent)
    {
        Squad? squad = null;
        foreach (Squad s in _squads)
        {
            if (s.Team == agent.Team && s.Members.Count < 8)
            {
                squad = s;
                break;
            }
        }
        if (squad is null)
        {
            squad = new Squad(agent.Team, _squads.Count(s => s.Team == agent.Team));
            _squads.Add(squad);
        }
        agent.Squad = _squads.IndexOf(squad);
        squad.Members.Add(agent);
    }

    /// <summary>
    /// Once a second: spread each team's squads over the zones, twice as many
    /// on zones the team does not hold as on ones it does.
    /// </summary>
    private void UpdateSquads()
    {
        if (_zones.Length == 0)
        {
            // Nothing to capture: each squad heads for a point where a fight is likely for a while, then
            // another, and sooner if the team is fighting somewhere else.
            if (!EnsureRoamFields())
                return;
            foreach (Squad s in _squads)
            {
                if (s.RoamTarget >= 0 && _now < s.RoamUntil && !FightMovedAway(s))
                    continue;
                int pick = ChooseRoamPoint(s.Team, 0.5f, ref _squadRng, null, s.RoamTarget);
                s.RoamTarget = pick >= 0 ? pick : _squadRng.NextInt(_roamPoints.Length);
                s.RoamSince = _now;
                s.RoamUntil = _now + _squadRng.Range(40f, 90f);
                // The squad is moving on: those who follow orders drop what they were heading for.
                foreach (Agent a in s.Members)
                {
                    if (a.Goal == Goal.Roam && a.Rng.NextFloat() < a.P.Obedience)
                    {
                        a.RoamTarget = -1;
                        a.NeedsDecision = true;
                    }
                }
            }
            return;
        }
        Span<int> have = stackalloc int[_zones.Length];
        Span<int> want = stackalloc int[_zones.Length];
        for (int team = 0; team < 256; team++)
        {
            int squads = 0;
            have.Clear();
            foreach (Squad s in _squads)
            {
                if (s.Team != team)
                    continue;
                squads++;
                if (s.Zone < 0)
                    SetSquadZone(s, s.Index % _zones.Length);
                have[s.Zone]++;
            }
            if (squads == 0)
                continue;
            float total = 0;
            for (int z = 0; z < _zones.Length; z++)
                total += _zones[z].Owner == team ? 1 : 2;
            for (int z = 0; z < _zones.Length; z++)
                want[z] = (int)MathF.Round(squads * (_zones[z].Owner == team ? 1 : 2) / total);
            foreach (Squad s in _squads)
            {
                if (s.Team != team || have[s.Zone] <= want[s.Zone])
                    continue;
                for (int z = 0; z < _zones.Length; z++)
                {
                    if (have[z] >= want[z])
                        continue;
                    have[s.Zone]--;
                    have[z]++;
                    SetSquadZone(s, z);
                    break;
                }
            }
        }
    }

    private void SetSquadZone(Squad squad, int zone)
    {
        squad.Zone = zone;
        foreach (Agent a in squad.Members)
        {
            // An order is a bias, not a command: some players go where they like.
            a.Zone = a.Rng.NextFloat() < (1f - a.P.Obedience) * 0.5f ? OwnChoiceOfZone(a, zone) : zone;
            if (a.Goal is Goal.GoZone or Goal.Hold)
                a.NeedsDecision = true;
        }
    }

    private int OwnChoiceOfZone(Agent a, int ordered)
    {
        if (a.EntityIndex < 0)
            return ordered;
        Vector2 position = PositionOf(a.EntityIndex);
        int best = ordered;
        float bestDistance = float.MaxValue;
        for (int z = 0; z < _zones.Length; z++)
        {
            if (_zones[z].Owner == a.Team)
                continue;
            float d = Vector2.DistanceSquared(position, _zones[z].Position);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = z;
            }
        }
        return best;
    }

    /// <summary>
    /// Picks what the agent is trying to do: fight what it sees, chase what it
    /// remembers, or play the objective. Scored, with the current goal
    /// favoured so it does not flip on small changes.
    /// </summary>
    private void Decide(Agent a)
    {
        a.NeedsDecision = false;
        if (a.EntityIndex < 0)
            return;
        Vector2 position = PositionOf(a.EntityIndex);

        // Fight: the nearest visible enemy, sticking with the current one unless another is much closer.
        int visible = -1;
        float nearest = float.MaxValue;
        for (int t = 0; t < MaxTracks; t++)
        {
            ref Track track = ref a.Tracks[t];
            if (track.State != TrackState.Visible)
                continue;
            float d = track.Distance * (a.Goal == Goal.Engage && track.Entity == a.TargetEntity ? 0.6f : 1f);
            if (d < nearest)
            {
                nearest = d;
                visible = t;
            }
        }
        if (visible >= 0)
        {
            AddContact(a.Team, a.Tracks[visible].Position);
            a.Goal = Goal.Engage;
            a.TargetTrack = visible;
            a.TargetEntity = a.Tracks[visible].Entity;
            return;
        }

        int remembered = -1;
        for (int t = 0; t < MaxTracks; t++)
        {
            if (a.Tracks[t].State == TrackState.Lost && (remembered < 0 || a.Tracks[t].SeenAt > a.Tracks[remembered].SeenAt))
                remembered = t;
        }
        float chase = 0;
        if (remembered >= 0)
        {
            float freshness = 1f - (float)((_now - a.Tracks[remembered].SeenAt) / a.P.MemorySeconds);
            chase = (0.35f + 0.5f * a.P.Aggression) * MathF.Max(0, freshness);
            if (a.Goal == Goal.Investigate)
                chase *= 1.15f;
        }
        if (_sites.Length > 0)
        {
            EnsureRole(a);
            if (_now >= a.SiteReviewAt || _now >= a.SiteRecheckAt)
                ReviewSite(a, position);
        }
        bool site = PlaysSite(a);
        // Seekers, and anyone with no objective to play, roam toward the fighting.
        bool roam = _zones.Length == 0 && !site && EnsureRoamFields();
        float objective = roam ? 0.45f : site ? SitePull(a) : _zones.Length > 0 && a.Zone >= 0 ? 0.45f + 0.3f * a.P.Obedience : 0;
        if (a.Goal is Goal.GoZone or Goal.Hold or Goal.Roam or Goal.Objective)
            objective *= 1.15f;

        float hunt = HuntScore(a);
        // Someone playing an objective does not go far from it after a killer or a sound.
        if (site && hunt > 0 && Vector2.Distance(a.HuntPoint, _sites[a.Site].Stand) > FarFromSite)
            hunt *= 0.35f;
        float camp = CampScore(a);

        if (chase > objective && chase > hunt && chase > camp && chase > 0)
        {
            ref Track track = ref a.Tracks[remembered];
            bool retarget = a.Goal != Goal.Investigate || a.TargetEntity != track.Entity
                || Vector2.DistanceSquared(a.PathGoal, track.Position) > 16f;
            if (retarget)
            {
                if (_pathBudget <= 0)
                {
                    a.NeedsDecision = true; // try again next tick
                    return;
                }
                _pathBudget--;
                Stats.PathRequests++;
                if (!_map.FindPath(Feet(a), new Vector3(track.Position, track.Height), a.Path, _options.MaxPathNodes))
                {
                    Stats.PathFailures++;
                    track.State = TrackState.Free;
                    a.NeedsDecision = true;
                    return;
                }
                a.PathIndex = 0;
                a.PathGoal = track.Position;
                a.HasSteer = false;
            }
            a.Goal = Goal.Investigate;
            a.TargetTrack = remembered;
            a.TargetEntity = track.Entity;
            return;
        }

        if (camp > objective && camp > hunt && camp > 0)
        {
            if (a.Goal != Goal.Camp)
                a.HasSteer = false;
            a.Goal = Goal.Camp;
            return;
        }

        if (hunt > objective && hunt > 0)
        {
            if (a.Goal != Goal.Hunt)
                a.HasSteer = false;
            a.Goal = Goal.Hunt;
            return;
        }

        if (site)
        {
            if (a.Goal != Goal.Objective)
            {
                a.HasSteer = false;
                a.HasPost = false;
            }
            a.Flow = _sites[a.Site].Flow;
            a.Goal = Goal.Objective;
            return;
        }

        if (roam)
        {
            if (a.RoamTarget < 0 || a.Goal != Goal.Roam)
                PickRoamTarget(a, position, own: false);
            a.Goal = Goal.Roam;
            return;
        }

        if (objective > 0)
        {
            ref Zone zone = ref _zones[a.Zone];
            float d = Vector2.Distance(position, zone.Position);
            Goal next = d < zone.Radius * 0.7f ? Goal.Hold
                : a.Goal == Goal.Hold && d < zone.Radius * 1.1f ? Goal.Hold
                : Goal.GoZone;
            if (next != a.Goal || a.Flow != _zoneFlow[a.Zone])
            {
                a.Flow = _zoneFlow[a.Zone];
                a.HasSteer = false;
                a.HasHoldPoint = false;
            }
            a.Goal = next;
            return;
        }
        a.Goal = Goal.None;
    }
}
