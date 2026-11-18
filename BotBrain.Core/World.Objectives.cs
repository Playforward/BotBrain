using System.Numerics;
using BotBrain.Maps;
using BotBrain.Protocol;

namespace BotBrain.Core;

/// <summary>
/// Objectives worked by hand (the <c>interact</c> primitive): stand at one
/// and hold the interact action to take it from the other side, or to stop
/// them taking it. A game's whole objective mode can be built from these
/// with <c>requires</c> (spec section 6.3): Sabotage is two of them, then a
/// third that needs both.
/// <para>What the brain reads from each, and nothing else: its owner (the
/// team whose it is now, 255 for nobody's) and its progress, which for this
/// primitive says how firmly the owner holds it: 1 when settled, below 1
/// while a claim is still running down (a hack holding, a charge armed),
/// when the other side can still undo it. Its <c>requires</c> are the
/// objectives a team must hold, settled, before it can work this one.</para>
/// <para>Roles. Per team: a few <see cref="Role.Seeker"/>s who play for
/// kills and ignore objectives, and the rest split between
/// <see cref="Role.Defender"/>s, who keep the team's objectives, and
/// <see cref="Role.Attacker"/>s, who take the other side's. Never half and
/// half: the team's natural side (defending, for the team that starts
/// owning more) gets 51 to 57% (the owner's numbers, 2026-11-17). Losing an
/// objective, a player goes either to the team's other objective of its
/// kind or to whichever objective is nearest (persona <c>Rotate</c>).</para>
/// </summary>
public sealed partial class World
{
    private const byte NoTeam = 255;
    /// <summary>Share of each team that ignores objectives. [guess] The owner asked for "a small number"; no study measures it (see JOURNAL 2026-11-17).</summary>
    private const float SeekerShareLow = 0.06f, SeekerShareHigh = 0.14f;
    /// <summary>The natural side's share of the rest (the owner's numbers).</summary>
    private const float MajorShareLow = 0.51f, MajorShareHigh = 0.57f;
    /// <summary>
    /// Holding interact this long with nothing happening means the game is
    /// refusing (MAG locks Sabotage's uplinks once C opens): let go, and the
    /// team leaves it until it next changes, or for a few minutes at most.
    /// </summary>
    private const float GiveUpWorkSeconds = 12f, RefusedSeconds = 240f;
    /// <summary>Progress at or above this is settled.</summary>
    private const float Settled = 0.999f;
    /// <summary>A hunt that leads further than this from the objective an agent plays pulls it much less.</summary>
    private const float FarFromSite = 60f;
    /// <summary>How far out a player stands guard over a teammate at work, metres.</summary>
    private const float GuardInner = 3f, GuardOuter = 10f;

    /// <summary>What an objective is to one team.</summary>
    private enum Stance : byte
    {
        /// <summary>The other side's, and this team cannot work it yet (or the game refused).</summary>
        Locked,
        /// <summary>The other side's (or nobody's), settled, and this team can work it: take it.</summary>
        Take,
        /// <summary>The other side's claim is running down: stop it.</summary>
        Stop,
        /// <summary>This team's claim is running down: guard it until it settles.</summary>
        Guard,
        /// <summary>This team's, settled, and the other side can work it now: defend it.</summary>
        Hold,
        /// <summary>This team's, settled, and nobody can work it yet.</summary>
        Calm,
        /// <summary>Locked for good: nobody's business any more.</summary>
        Done,
    }

    private struct Site
    {
        public int Id;
        /// <summary>Where the thing is, and the nearest walkable spot to it.</summary>
        public Vector2 Position, Stand;
        /// <summary>The thing's height, and the height of the floor at the stand spot.</summary>
        public float Height, StandZ, Radius;
        /// <summary>Whether a body can stand within reach of it: if not (on a floor the map does not have), bots fight round it but cannot work it.</summary>
        public bool Reachable;
        public int Owner;
        public float Progress;
        /// <summary>Nobody can work it any more (the body says so): it needs neither taking nor keeping.</summary>
        public bool Locked;
        /// <summary>Indices of the sites a team must hold, settled, to work this one.</summary>
        public int[] Requires;
        public FlowField Flow;
        /// <summary>The agent (slot) at work on it, and when it last was.</summary>
        public int Worker;
        public double WorkerAt;
    }

    private sealed class TeamPlan(float seekers, float major, Role majorRole)
    {
        public readonly float Seekers = seekers, Major = major;
        public readonly Role MajorRole = majorRole;
    }

    private Site[] _sites = [];
    private readonly Dictionary<byte, TeamPlan> _teamPlans = [];
    /// <summary>Until when each team leaves a site alone after the game refused it (team * sites + site).</summary>
    private readonly Dictionary<int, double> _refused = [];
    private readonly List<byte> _teamIds = [];
    private Rng _roleRng;

    private void SetSites(ObjectivesMessage message)
    {
        _roleRng = new Rng(_options.Seed * 0x9E3779B97F4A7C15UL + 0x5173UL);
        List<ObjectiveInfo> infos = [.. message.Objectives.Where(o => o.Primitive == "interact")];
        Site[] sites = new Site[infos.Count];
        for (int i = 0; i < infos.Count; i++)
        {
            ObjectiveInfo o = infos[i];
            Vector2 at = new(o.X, o.Y);
            float radius = MathF.Max(0.5f, o.Radius);
            (Vector2 stand, float standZ, bool reachable) = StandFor(at, o.Z, radius);
            sites[i] = new Site
            {
                Id = o.Id,
                Position = at,
                Stand = stand,
                StandZ = standZ,
                Reachable = reachable,
                Height = o.Z,
                Radius = radius,
                Owner = o.OwnerTeam,
                Progress = o.Progress,
                Locked = o.Locked,
                Requires = [.. o.Requires.Select(r => infos.FindIndex(x => x.Id == r)).Where(r => r >= 0)],
                Flow = _map.BuildFlowField(new Vector3(stand, standZ)),
                Worker = -1,
            };
        }
        _sites = sites;
        SiteReport = sites.Length == 0 ? null : string.Join("; ", sites.Select(s =>
            $"objective {s.Id} at ({s.Position.X:F0}, {s.Position.Y:F0}, {s.Height:F0}): "
            + (s.Reachable ? $"workable from {Vector2.Distance(s.Position, s.Stand):F1} m" : $"NOT workable, nearest ground {Vector2.Distance(s.Position, s.Stand):F1} m off at height {s.StandZ:F0}")
            + $", {(float.IsFinite(s.Flow.DistanceFrom(SpawnOf(s.Owner))) ? "reached" : "NOT reached")} from its owner's spawn"));
        _teamPlans.Clear();
        _refused.Clear();
        _approaches.Clear();
        foreach (Agent a in _agents)
        {
            a.Site = -1;
            a.SiteReviewAt = _now;
        }
    }

    /// <summary>What the brain made of the interact objectives, for the host's log: where bots can stand to work each.</summary>
    public string? SiteReport { get; private set; }

    /// <summary>
    /// Where to stand to work something at <paramref name="at"/>, height
    /// <paramref name="height"/>: the nearest walkable cell within 12 m whose
    /// ground is near the thing's own height (a console on a floor the map
    /// does not have is not worked from the street below it). Reachable when
    /// that cell is within the reach the game gave.
    /// </summary>
    private (Vector2 Stand, float Z, bool Reachable) StandFor(Vector2 at, float height, float reach)
    {
        if (_map.Nav is { } nav)
        {
            // On the mesh: the floor under the thing, from 2.6 m below it to a little above, within 12 m.
            if (nav.Floor(new Vector3(at, height), out _, out Vector3 floor, across: 12f, below: 2.6f, above: 0.3f))
                return (Flat(floor), floor.Z, Vector2.Distance(Flat(floor), at) <= reach);
            return _map.SnapToFloor(new Vector3(at, height), out Vector3 near) ? (Flat(near), near.Z, false) : (at, height, false);
        }
        Vector2 best = at, any = at;
        float bestScore = float.PositiveInfinity, anyDistance = float.PositiveInfinity;
        int cx = _map.CellX(at.X), cy = _map.CellY(at.Y), r = (int)MathF.Ceiling(12f / _map.CellSize);
        for (int y = cy - r; y <= cy + r; y++)
        {
            for (int x = cx - r; x <= cx + r; x++)
            {
                if (_map.IsBlockedCell(x, y))
                    continue;
                Vector2 c = _map.CellCenter(x, y);
                float d = Vector2.Distance(c, at);
                if (d > 12f)
                    continue;
                if (d < anyDistance)
                {
                    anyDistance = d;
                    any = c;
                }
                // Feet a little below the thing worked, at most a step or two off.
                float rise = _map.HasGround ? MathF.Abs(_map.GroundAt(c) + 1f - height) : 0;
                if (rise > 2.5f || d >= bestScore)
                    continue;
                bestScore = d;
                best = c;
            }
        }
        if (float.IsFinite(bestScore))
            return (best, _map.HasGround ? _map.GroundAt(best) : 0, bestScore <= reach);
        return (any, _map.HasGround ? _map.GroundAt(any) : 0, false);
    }

    private Vector2 SpawnOf(int team)
    {
        foreach (MapPoint p in _map.PointsOf(MapPointKind.Spawn))
            if (p.Team == team)
                return p.Position;
        return default;
    }

    /// <summary>An objective event for an interact objective: a change of owner, or of whether it is settled, has everyone playing objectives think again.</summary>
    private void OnSiteEvent(int id, int owner, float progress, ObjectiveFlags flags)
    {
        for (int i = 0; i < _sites.Length; i++)
        {
            ref Site s = ref _sites[i];
            if (s.Id != id)
                continue;
            bool locked = (flags & ObjectiveFlags.Locked) != 0;
            bool changed = s.Owner != owner || (s.Progress >= Settled) != (progress >= Settled) || s.Locked != locked;
            s.Owner = owner;
            s.Progress = progress;
            s.Locked = locked;
            if (changed)
                SiteChanged(i);
        }
    }

    /// <summary>
    /// Everyone playing objectives hears of it (the game tells its players;
    /// MAG's HUD does), each after a moment, and thinks again. Whatever the
    /// game refused before may be open now.
    /// </summary>
    private void SiteChanged(int site)
    {
        Stats.SiteChanges++;
        foreach (byte team in _teamIds)
            _refused.Remove(team * _sites.Length + site);
        foreach (Agent a in _agents)
        {
            if (a.Role is not (Role.Defender or Role.Attacker))
                continue;
            // [guess] Taking in a HUD change and deciding: half a second to a few.
            double at = _now + a.Rng.Range(0.4f, 2.5f);
            if (at < a.SiteReviewAt)
                a.SiteReviewAt = at;
        }
    }

    private TeamPlan PlanFor(byte team)
    {
        if (_teamPlans.TryGetValue(team, out TeamPlan? plan))
            return plan;
        int owned = 0, others = 0;
        foreach (Site s in _sites)
        {
            if (s.Owner == team)
                owned++;
            else
                others++;
        }
        Role major = owned > others ? Role.Defender
            : owned < others ? Role.Attacker
            : _roleRng.NextFloat() < 0.5f ? Role.Defender : Role.Attacker;
        plan = new TeamPlan(_roleRng.Range(SeekerShareLow, SeekerShareHigh), _roleRng.Range(MajorShareLow, MajorShareHigh), major);
        _teamPlans[team] = plan;
        return plan;
    }

    /// <summary>Gives a new agent the role its team is shortest of, against the team's plan.</summary>
    private void EnsureRole(Agent a)
    {
        if (a.Role != Role.None || _sites.Length == 0)
            return;
        TeamPlan plan = PlanFor(a.Team);
        Role minorRole = plan.MajorRole == Role.Defender ? Role.Attacker : Role.Defender;
        int seekers = 0, major = 0, minor = 0;
        foreach (Agent o in _agents)
        {
            if (o == a || o.Team != a.Team)
                continue;
            if (o.Role == Role.Seeker)
                seekers++;
            else if (o.Role == plan.MajorRole)
                major++;
            else if (o.Role == minorRole)
                minor++;
        }
        // Whole-number targets for the team with this agent in it: the natural
        // side always more than half of those playing objectives, never level.
        int n = seekers + major + minor + 1;
        int wantSeekers = (int)MathF.Round(n * plan.Seekers);
        int playing = n - wantSeekers;
        int wantMajor = Math.Min(playing, Math.Max(playing / 2 + 1, (int)MathF.Round(playing * plan.Major)));
        int wantMinor = playing - wantMajor;
        int shortMajor = wantMajor - major, shortMinor = wantMinor - minor, shortSeekers = wantSeekers - seekers;
        a.Role = shortMajor >= shortMinor && shortMajor >= shortSeekers ? plan.MajorRole
            : shortMinor >= shortSeekers ? minorRole
            : Role.Seeker;
        Stats.Roles[(int)a.Role]++;
        a.Site = -1;
        a.SiteReviewAt = _now;
        a.SiteRecheckAt = _now + a.Rng.Range(35f, 70f);
        RecordRole(a);
    }

    private Stance StanceOf(int site, byte team)
    {
        ref Site s = ref _sites[site];
        if (s.Locked)
            return Stance.Done;
        bool settled = s.Progress >= Settled;
        if (s.Owner == team || (s.Owner != NoTeam && !_hostile[team * 256 + s.Owner]))
            return !settled ? Stance.Guard : Threatened(site, team) ? Stance.Hold : Stance.Calm;
        Stance stance = !settled && s.Owner != NoTeam ? Stance.Stop : Workable(site, team) ? Stance.Take : Stance.Locked;
        if (stance != Stance.Locked && _refused.TryGetValue(team * _sites.Length + site, out double until) && _now < until)
            return Stance.Locked;
        return stance;
    }

    private bool Workable(int site, int team)
    {
        foreach (int r in _sites[site].Requires)
        {
            if (_sites[r].Owner != team || _sites[r].Progress < Settled)
                return false;
        }
        return true;
    }

    private bool Threatened(int site, byte team)
    {
        foreach (byte other in _teamIds)
        {
            if (other != team && _hostile[team * 256 + other] && Workable(site, other))
                return true;
        }
        return false;
    }

    /// <summary>How much a role wants an objective in a stance; 0 for not its business.</summary>
    private static float RoleWeight(Role role, Stance stance) => (role, stance) switch
    {
        (Role.Defender or Role.Attacker, Stance.Stop) => 3f,
        (Role.Defender or Role.Attacker, Stance.Guard) => 1.6f,
        (Role.Defender, Stance.Hold) => 1f,
        (Role.Defender, Stance.Calm) => 0.25f,
        (Role.Attacker, Stance.Take) => 1f,
        _ => 0f,
    };

    /// <summary>The other role's business, for when there is none of its own: a defender retakes, an attacker keeps what it took.</summary>
    private static float FallbackWeight(Role role, Stance stance) => (role, stance) switch
    {
        (Role.Defender, Stance.Take) => 1f,
        (Role.Attacker, Stance.Hold) => 1f,
        (Role.Attacker, Stance.Calm) => 0.25f,
        _ => 0f,
    };

    /// <summary>
    /// Picks an objective for an agent. By need: its own kind of business
    /// (the other kind if there is none), the urgent first, spread so a team
    /// does not all go to one, nearer ones likelier, and the current one
    /// favoured. Nearest: the closest objective it has any business at.
    /// </summary>
    private int ChooseSite(Agent a, Vector2 position, bool nearest, int avoid)
    {
        Vector3 feet = new(position, _entities[a.EntityIndex].Z);
        int n = _sites.Length;
        Span<float> own = stackalloc float[n];
        Span<float> other = stackalloc float[n];
        bool anyOwn = false, anyOther = false;
        for (int i = 0; i < n; i++)
        {
            if (i == avoid)
                continue;
            Stance stance = StanceOf(i, a.Team);
            // The end of a chain (what the others were taken for) counts double once it is in play.
            float chain = _sites[i].Requires.Length > 0 && stance is Stance.Take or Stance.Hold ? 2f : 1f;
            own[i] = RoleWeight(a.Role, stance) * chain;
            other[i] = FallbackWeight(a.Role, stance) * chain;
            anyOwn |= own[i] > 0;
            anyOther |= other[i] > 0;
        }
        if (!anyOwn && !anyOther)
            return -1;
        if (nearest)
        {
            int best = -1;
            float bestDistance = float.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                if (own[i] <= 0 && other[i] <= 0)
                    continue;
                float d = _sites[i].Flow.DistanceFrom(feet);
                if (!float.IsFinite(d))
                    d = 10000 + Vector2.Distance(position, _sites[i].Stand);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = i;
                }
            }
            return best;
        }
        Span<float> weight = anyOwn ? own : other;
        // How many teammates already play each, and how many there would be if they were spread evenly.
        Span<int> playing = stackalloc int[n];
        int players = 0, candidates = 0;
        foreach (Agent o in _agents)
        {
            if (o == a || o.Team != a.Team || o.Role is not (Role.Defender or Role.Attacker))
                continue;
            players++;
            if ((uint)o.Site < (uint)n)
                playing[o.Site]++;
        }
        // Each objective's fair share of the team: in proportion to how much it matters, so a
        // quiet one (C before it opens) is left to a few, not an even third.
        float sum = 0;
        for (int i = 0; i < n; i++)
        {
            if (weight[i] > 0)
            {
                candidates++;
                sum += weight[i];
            }
        }
        float total = 0;
        for (int i = 0; i < n; i++)
        {
            if (weight[i] <= 0)
                continue;
            float share = MathF.Max(1f, players * weight[i] / sum);
            float d = _sites[i].Flow.DistanceFrom(feet);
            float far = float.IsFinite(d) ? d : 1000f;
            weight[i] *= 1f / (1f + playing[i] / share) / (1f + far / 150f);
            // One the map cannot stand anyone at: fought round, never worked. Rather one that can be.
            if (!_sites[i].Reachable && StanceOf(i, a.Team) is Stance.Take or Stance.Stop)
                weight[i] *= 0.3f;
            // People stick with what they are doing unless there is a reason.
            if (i == a.Site)
                weight[i] *= 4f;
            total += weight[i];
        }
        float pick = a.Rng.NextFloat() * total;
        int last = -1;
        for (int i = 0; i < n; i++)
        {
            if (weight[i] <= 0)
                continue;
            last = i;
            pick -= weight[i];
            if (pick <= 0)
                return i;
        }
        return last;
    }

    /// <summary>
    /// Thinks again about which objective to play: after a change in the
    /// match (noticed a moment later), and every half minute or so anyway.
    /// The one it plays no longer its business (lost, or won): by persona,
    /// to the team's other one of its kind, or to the nearest it has any
    /// business at (a defender's lost objective is usually that: it goes to
    /// retake it).
    /// </summary>
    private void ReviewSite(Agent a, Vector2 position)
    {
        a.SiteReviewAt = double.MaxValue;
        if (_now >= a.SiteRecheckAt)
            a.SiteRecheckAt = _now + a.Rng.Range(35f, 70f);
        int before = a.Site;
        if ((uint)a.Site >= (uint)_sites.Length)
            a.Site = ChooseSite(a, position, nearest: false, avoid: -1);
        else if (!StillItsBusiness(a))
        {
            bool rotate = a.Rng.NextFloat() < a.P.Rotate;
            // An attacker's objective stops being its business by being won: it moves on, not back.
            int next = ChooseSite(a, position, nearest: !rotate, avoid: rotate || a.Role == Role.Attacker ? a.Site : -1);
            a.Site = next >= 0 ? next : ChooseSite(a, position, nearest: true, avoid: -1);
            if (a.Site != before)
                Stats.SiteLosses++;
        }
        else
            a.Site = ChooseSite(a, position, nearest: false, avoid: -1);
        if (a.Site == before)
            return;
        Stats.SiteSwitches++;
        a.HasPost = false;
        a.Interacting = false;
        a.HasSteer = false;
        a.NeedsDecision = true;
        // Sitting on a camp spot while the objective is being taken: up and go.
        if (a.CampSpot >= 0 && a.Site >= 0 && StanceOf(a.Site, a.Team) == Stance.Stop)
            EndCamp(a);
        RecordRole(a);
    }

    /// <summary>
    /// Whether the objective it plays is still its business: its role's, or,
    /// when the role has nothing anywhere (the attacking team's defenders
    /// before anything is taken), the other role's.
    /// </summary>
    private bool StillItsBusiness(Agent a)
    {
        if (RoleWeight(a.Role, StanceOf(a.Site, a.Team)) > 0)
            return true;
        if (FallbackWeight(a.Role, StanceOf(a.Site, a.Team)) <= 0)
            return false;
        for (int i = 0; i < _sites.Length; i++)
        {
            if (RoleWeight(a.Role, StanceOf(i, a.Team)) > 0)
                return false; // its own kind of business has come up: go to that
        }
        return true;
    }

    /// <summary>How much an agent wants to play its objective, compared like the other goals in Decide.</summary>
    private float SitePull(Agent a)
    {
        float urgency = StanceOf(a.Site, a.Team) switch
        {
            Stance.Stop => 1.6f,
            Stance.Guard => 1.25f,
            Stance.Calm => 0.8f,
            _ => 1f,
        };
        return (0.5f + 0.3f * a.P.Obedience) * urgency;
    }

    /// <summary>Whether an agent plays objectives in this match, and has one to play.</summary>
    private bool PlaysSite(Agent a) => _sites.Length > 0 && a.Role is Role.Defender or Role.Attacker && (uint)a.Site < (uint)_sites.Length;

    /// <summary>
    /// The objective skill. Something to work and nobody at it: go there and
    /// hold interact until it changes (or the game plainly refuses). Someone
    /// already at it: stand guard close by. Nothing to work: defenders keep a
    /// post out from it, looking the way the enemy comes; guards stay closer.
    /// </summary>
    private void PlaySite(Agent a, ref EntityRecord me, Vector2 position)
    {
        if (!PlaysSite(a))
        {
            a.Goal = Goal.None;
            a.NeedsDecision = true;
            LookAround(a, ref me, position);
            return;
        }
        ref Site s = ref _sites[a.Site];
        a.Flow = s.Flow;
        Stance stance = StanceOf(a.Site, a.Team);
        float straight = Vector2.Distance(position, s.Stand);
        bool work = stance is Stance.Take or Stance.Stop && s.Reachable && _now >= a.InteractCooldownUntil;
        bool someoneElse = s.Worker >= 0 && s.Worker != a.Slot && _now - s.WorkerAt < 0.6;
        if (work && !someoneElse)
        {
            if (straight > MathF.Max(0.6f, s.Radius * 0.6f))
            {
                a.Interacting = false;
                Travel(a, ref me, position);
                if (straight < 12f)
                    a.Intent.Sprint = false;
                return;
            }
            s.Worker = a.Slot;
            s.WorkerAt = _now;
            if (!a.Interacting)
            {
                a.Interacting = true;
                a.InteractSince = _now;
                Stats.Interacts++;
            }
            else if (_now - a.InteractSince > GiveUpWorkSeconds)
            {
                // Nothing happened: the game will not have it. The team leaves it a while.
                a.Interacting = false;
                a.InteractCooldownUntil = _now + a.Rng.Range(4f, 8f);
                _refused[a.Team * _sites.Length + a.Site] = _now + RefusedSeconds;
                Stats.Refusals++;
                a.SiteReviewAt = _now;
                s.Worker = -1;
                return;
            }
            a.Intent.Interact = true;
            if (straight > s.Radius * 0.3f)
            {
                a.Intent.Move = Vector2.Normalize(s.Stand - position);
                a.Intent.Speed = 0.3f;
            }
            // Eyes on the work, now and then a glance up and round.
            if (_now >= a.ScanUntil)
            {
                bool glance = a.Rng.NextFloat() < 0.3f * a.P.ScanHabit;
                Vector2 toWork = s.Position - position;
                float yaw = toWork.LengthSquared() > 0.01f ? MathF.Atan2(toWork.Y, toWork.X) : me.Yaw;
                a.ScanYaw = glance ? yaw + a.Rng.Range(-2f, 2f) : yaw + a.Rng.Gaussian() * 0.15f;
                a.ScanUntil = _now + (glance ? a.Rng.Range(0.4f, 0.9f) : a.Rng.Range(1f, 2.5f));
            }
            a.Intent.HasLook = true;
            a.Intent.LookPoint = new Vector3(position + new Vector2(MathF.Cos(a.ScanYaw), MathF.Sin(a.ScanYaw)) * 3f, me.Z + 0.9f);
            return;
        }
        a.Interacting = false;
        bool defending = stance is Stance.Hold or Stance.Calm;
        // [guess] Defenders hang 6 to 22 m out, the bolder further; MAG's developers
        // warned of fights 100 m out, so nobody plays it from that far.
        float inner = defending ? 6f : GuardInner;
        float outer = defending ? 10f + 12f * a.P.Aggression : GuardOuter;
        float walk = s.Flow.DistanceFrom(new Vector3(position, me.Z));
        if (!float.IsFinite(walk) || walk > outer + 10f)
        {
            Travel(a, ref me, position);
            if (straight < 15f)
                a.Intent.Sprint = false;
            return;
        }
        Post(a, ref me, position, ref s, inner, outer);
    }

    /// <summary>A spot near an objective to hold for a while, facing out the way the enemy comes; moved every few seconds, as people shift.</summary>
    private void Post(Agent a, ref EntityRecord me, Vector2 position, ref Site s, float inner, float outer)
    {
        if (!a.HasPost || _now >= a.PostUntil)
        {
            // Mostly on the side the enemy comes from, between them and the objective.
            float toward = ThreatYaw(a.Team, a.Site, s.Stand);
            for (int attempt = 0; attempt < 10; attempt++)
            {
                float yaw = toward + a.Rng.Gaussian() * 0.8f;
                float r = a.Rng.Range(inner, outer);
                Vector2 candidate = s.Stand + new Vector2(MathF.Cos(yaw), MathF.Sin(yaw)) * r;
                if (!_map.Standable(new Vector3(candidate, s.StandZ)))
                    continue;
                // In plain sight of it and a straight walk out from it, so the way there is: to it, then out.
                if (!_map.ClearPath(new Vector3(s.Stand, s.StandZ), new Vector3(candidate, s.StandZ)))
                    continue;
                a.PostPoint = candidate;
                // Facing the way the enemy comes, from where it stands (not merely away from the objective).
                a.PostLook = ThreatYaw(a.Team, a.Site, candidate);
                a.HasPost = true;
                break;
            }
            a.PostUntil = _now + a.Rng.Range(6f, 18f);
        }
        if (a.HasPost)
        {
            Vector2 to = a.PostPoint - position;
            float length = to.Length();
            if (length > 1f)
            {
                Vector3 feet = new(position, me.Z);
                Vector2 toward = _map.ClearPath(feet, new Vector3(a.PostPoint, s.StandZ)) ? to : s.Flow.SteerPoint(feet) - position;
                if (toward.LengthSquared() > 1e-4f)
                {
                    Vector2 direction = Vector2.Normalize(toward);
                    CheckStuck(a, position, ref direction);
                    a.Intent.Move = direction;
                    a.Intent.Speed = length > 8f ? 1f : 0.55f;
                }
            }
        }
        if (_now >= a.ScanUntil)
        {
            a.ScanYaw = WrapAngle(a.PostLook + Math.Clamp(a.Rng.Gaussian() * 0.6f * a.P.ScanHabit, -1.4f, 1.4f) + (a.Rng.NextFloat() < 0.06f ? MathF.PI : 0));
            a.ScanUntil = _now + a.Rng.Range(1f, 3f) / (0.5f + a.P.ScanHabit);
        }
        a.Intent.HasLook = true;
        a.Intent.LookPoint = new Vector3(position + new Vector2(MathF.Cos(a.ScanYaw), MathF.Sin(a.ScanYaw)) * 20f, me.Z + EyeHeight - 0.1f);
    }

    /// <summary>
    /// Which way the enemy comes to an objective from, looking from
    /// <paramref name="at"/>: toward where their routes from their spawns
    /// arrive (<see cref="Approaches"/>), and toward where the team met them
    /// nearby in the last 20 s. Older fights count for nothing: for attackers
    /// holding what they just took, those were their own way in, behind them
    /// (the owner saw attackers guarding A and B facing the wrong way).
    /// </summary>
    private float ThreatYaw(byte team, int site, Vector2 at)
    {
        Vector2 sum = default;
        foreach (Vector2 p in Approaches(team, site))
        {
            Vector2 d = p - at;
            float distance = d.Length();
            if (distance > 1f)
                sum += d / distance;
        }
        foreach (Contact c in _contacts)
        {
            if (c.Team != team)
                continue;
            Vector2 d = c.Position - at;
            float distance = d.Length();
            float age = (float)(_now - c.At);
            if (distance < 2f || distance > 60f || age > 20f)
                continue;
            sum += d / distance * (1f - age / 20f) * 1.5f;
        }
        return sum.LengthSquared() > 1e-6f ? MathF.Atan2(sum.Y, sum.X) : 0f;
    }

    private readonly Dictionary<int, Vector2[]> _approaches = [];

    /// <summary>
    /// Where the enemy's routes reach an objective: for each group of their
    /// spawns, the point about 15 m out along the walk from there to the
    /// objective. Found once per team and objective (a route search each).
    /// </summary>
    private Vector2[] Approaches(byte team, int site)
    {
        int key = team * 4096 + site;
        if (_approaches.TryGetValue(key, out Vector2[]? known))
            return known;
        Site s = _sites[site];
        Vector3 stand = new(s.Stand, s.StandZ);
        List<Vector2> points = [];
        List<Vector3> route = [];
        HashSet<long> groups = [];
        foreach (MapPoint p in _map.PointsOf(MapPointKind.Spawn))
        {
            if (p.Team == team || !_hostile[team * 256 + p.Team])
                continue;
            // One route per group of spawns (30 m squares).
            if (!groups.Add(((long)MathF.Floor(p.Position.X / 30f) << 32) ^ (uint)(int)MathF.Floor(p.Position.Y / 30f)))
                continue;
            if (!_map.FindPath(_map.PointAt(p), stand, route, _options.MaxPathNodes * 4) || route.Count == 0)
                continue;
            // Back along the route from the objective until 15 m out.
            Vector2 last = s.Stand, approach = new(route[0].X, route[0].Y);
            for (int k = route.Count - 1; k >= 0; k--)
            {
                Vector2 c = new(route[k].X, route[k].Y);
                if (Vector2.Distance(c, s.Stand) >= 15f)
                {
                    Vector2 dir = c - last;
                    float need = 15f - Vector2.Distance(last, s.Stand);
                    approach = dir.LengthSquared() > 1e-6f && need > 0 ? last + Vector2.Normalize(dir) * MathF.Min(need, dir.Length()) : c;
                    break;
                }
                last = c;
            }
            points.Add(approach);
        }
        if (points.Count == 0)
        {
            // No route found: straight toward their spawns.
            foreach (MapPoint p in _map.PointsOf(MapPointKind.Spawn))
                if (p.Team != team && _hostile[team * 256 + p.Team])
                    points.Add(p.Position);
        }
        Vector2[] result = [.. points];
        _approaches[key] = result;
        return result;
    }
}
