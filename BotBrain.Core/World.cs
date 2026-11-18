using System.Diagnostics;
using System.Numerics;
using BotBrain.Maps;
using BotBrain.Protocol;

namespace BotBrain.Core;

public sealed class BrainOptions
{
    /// <summary>Run every agent at full fidelity whether or not a human can see it (the worst case).</summary>
    public bool ForceFullFidelity { get; set; }
    public ulong Seed { get; set; } = 1;
    /// <summary>Individual route searches allowed per tick; the rest wait a tick.</summary>
    public int MaxPathRequestsPerTick { get; set; } = 2;

    /// <summary>
    /// Cells one route search may expand before giving up. Bounds the cost of
    /// a single search: chasing something far away across the map is not
    /// worth a slow tick, and the agent goes back to its objective instead.
    /// </summary>
    public int MaxPathNodes { get; set; } = 6000;

    /// <summary>Folder to record each match into for the replay viewer; null or empty for no recording.</summary>
    public string? RecordDirectory { get; set; }

    /// <summary>Record one tick in this many. 0 chooses: every tick up to 64 players, every third above.</summary>
    public int RecordEveryTicks { get; set; }
}

/// <summary>
/// One live match. Everything the brain knows and decides about it happens
/// here, on the thread that delivers its OBSERVE frames.
/// </summary>
public sealed partial class World
{
    internal const int MaxTracks = 4;
    internal const float EyeHeight = 1.6f, ChestHeight = 1.2f;
    internal const float ViewRange = 120f, HearRange = 60f, BucketSize = 32f;

    private readonly GridMap _map;
    private readonly BrainOptions _options;
    private readonly int _tickHz;

    // Entities, as last reported.
    private EntityRecord[] _entities = new EntityRecord[512];
    private uint[] _entitySeenTick = new uint[512];
    private int[] _agentOfEntity = new int[512];
    /// <summary>When each entity last fired, as far as the brain heard.</summary>
    private double[] _shotAt = new double[512];
    private int _entityCount;
    private readonly Dictionary<uint, int> _entityIndex = [];

    // Uniform grid over living players, rebuilt every tick.
    private readonly int _bucketsX, _bucketsY;
    private readonly int[] _bucketStart;
    private int[] _bucketItems = new int[512];
    private int[] _near = new int[512];

    private readonly List<Agent> _agents = [];
    private Agent?[] _agentBySlot = new Agent?[1024];
    private ObserverRecord[] _observers = new ObserverRecord[8];
    private int _observerCount;
    private readonly bool[] _hostile = new bool[256 * 256];

    private readonly uint _bitFire, _bitSprint, _bitReload, _bitInteract;
    private readonly float _maxTurn, _maxPitch, _deadzone, _inverseExponent;

    private double _now;
    private uint _tick;
    private bool _started;
    private int _pathBudget;

    public int Id { get; }
    public BrainStats Stats { get; } = new();
    public int AgentCount => _agents.Count;
    internal IReadOnlyList<Agent> Agents => _agents;

    public World(int id, Manifest manifest, GridMap map, BrainOptions options)
    {
        if (!manifest.ControlModes.TryGetValue("foot_shooter", out ControlModeSpec? mode))
            throw new ArgumentException("this build of the brain only drives the foot_shooter control mode");
        Id = id;
        _map = map;
        _options = options;
        _tickHz = Math.Max(1, manifest.TickHz);
        _bitFire = BitOf(mode, "fire");
        _bitSprint = BitOf(mode, "sprint");
        _bitReload = BitOf(mode, "reload");
        _bitInteract = BitOf(mode, "interact");
        foreach (int team in manifest.Teams.Ids)
            _teamIds.Add((byte)team);
        _maxTurn = mode.Look.MaxTurnDegS * MathF.PI / 180f;
        _maxPitch = mode.Look.MaxPitchDegS * MathF.PI / 180f;
        _deadzone = mode.Look.Deadzone;
        _inverseExponent = 1f / MathF.Max(0.1f, mode.Look.CurveExponent);
        foreach (int[] pair in manifest.Teams.Hostile)
        {
            if (pair.Length != 2)
                continue;
            _hostile[(byte)pair[0] * 256 + (byte)pair[1]] = true;
            _hostile[(byte)pair[1] * 256 + (byte)pair[0]] = true;
        }
        _bucketsX = Math.Max(1, (int)MathF.Ceiling(map.WorldWidth / BucketSize));
        _bucketsY = Math.Max(1, (int)MathF.Ceiling(map.WorldHeight / BucketSize));
        _bucketStart = new int[_bucketsX * _bucketsY + 1];
        StartRecording(manifest);
    }

    private static uint BitOf(ControlModeSpec mode, string action)
    {
        int index = mode.Actions.IndexOf(action);
        return index is >= 0 and < 32 ? 1u << index : 0;
    }

    /// <summary>Where an agent's feet are, with their height (which floor it is on).</summary>
    private Vector3 Feet(Agent a) => new(PositionOf(a.EntityIndex), _entities[a.EntityIndex].Z);

    private static Vector2 Flat(Vector3 p) => new(p.X, p.Y);

    /// <summary>Seats a new persona in a slot and returns it.</summary>
    public Persona AddAgent(int slot, int team)
    {
        Persona persona = Persona.Generate(_options.Seed * 1000003UL + (ulong)slot * 7919UL + 17);
        Agent agent = new(slot, (byte)team, persona);
        if (slot >= _agentBySlot.Length)
            Array.Resize(ref _agentBySlot, Math.Max(slot + 1, _agentBySlot.Length * 2));
        _agentBySlot[slot] = agent;
        _agents.Add(agent);
        AssignSquad(agent);
        RecordAgent(agent);
        return persona;
    }

    /// <summary>
    /// The body took a bot out (a person needed its seat, or the match is
    /// shrinking): forget it. Its slot is not reused by the body.
    /// </summary>
    /// <summary>The newest agent fault, for the host's log; see <see cref="BrainStats.Faults"/>.</summary>
    public string? LastFault { get; private set; }

    /// <summary>
    /// An agent update threw. That is a bug, but one bot's bug must not end the
    /// link: the body would take every bot out (live 2026-11-16, all 63 at once).
    /// Count it, keep the text for the log, and put the agent back to no goal.
    /// </summary>
    private void Fault(Agent a, string phase, Exception e)
    {
        Stats.Faults++;
        LastFault = $"{phase} slot {a.Slot} goal {a.Goal} camp {a.CampSpot} hunt {a.Hunt}: {e}";
        a.Goal = Goal.None;
        a.Hunt = HuntReason.None;
        a.HasSteer = false;
        a.Path.Clear();
        a.NeedsDecision = true;
    }

    public void RemoveAgent(int slot)
    {
        Agent? agent = slot >= 0 && slot < _agentBySlot.Length ? _agentBySlot[slot] : null;
        if (agent is null)
            return;
        EndCamp(agent);
        _agentBySlot[slot] = null;
        _agents.Remove(agent);
        foreach (Squad squad in _squads)
            squad.Members.Remove(agent);
        // Agents are found from entities by their place in the list, which has just shifted.
        Array.Fill(_agentOfEntity, -1, 0, _entityCount);
        for (int i = 0; i < _agents.Count; i++)
        {
            if (_agents[i].EntityIndex >= 0)
                _agentOfEntity[_agents[i].EntityIndex] = i;
        }
        Stats.AgentsLeft++;
    }

    /// <summary>Runs one tick: takes in ground truth, updates every agent, and fills the controls to send back.</summary>
    public void Tick(ObserveFrame frame, ActFrame act)
    {
        long t0 = Stopwatch.GetTimestamp();
        double now = frame.TimeMs / 1000.0;
        float dt = _started ? (float)Math.Clamp(now - _now, 0.001, 0.25) : 1f / _tickHz;
        _started = true;
        _now = now;
        _tick = frame.Tick;
        _pathBudget = _options.MaxPathRequestsPerTick;
        long raysBefore = _map.RaysCast + (_map.Mesh?.RaysCast ?? 0);

        Ingest(frame);
        if (_tick % (uint)_tickHz == 0)
        {
            UpdateSquads();
            UpdateContacts();
        }
        if (_tick % 15 == 0)
            UpdateFidelity();
        long t1 = Stopwatch.GetTimestamp();

        foreach (Agent a in _agents)
        {
            if (!a.Alive)
                continue;
            uint period = a.Tier == Tier.Full ? 3u : 6u;
            if ((_tick + (uint)a.Slot) % period == 0)
                Perceive(a);
        }
        long t2 = Stopwatch.GetTimestamp();

        foreach (Agent a in _agents)
        {
            if (a.Alive && (a.NeedsDecision || (_tick + (uint)a.Slot * 3) % 8 == 0))
            {
                try
                {
                    Decide(a);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    Fault(a, "decide", e);
                }
            }
        }
        long t3 = Stopwatch.GetTimestamp();

        act.Tick = frame.Tick;
        act.Count = 0;
        act.CommandCount = 0;
        foreach (Agent a in _agents)
        {
            if (a.EntityIndex < 0)
                continue;
            if (!a.Alive)
            {
                if (!a.SentNeutral)
                {
                    a.Sent = new ActRecord { Slot = (ushort)a.Slot, FromTick = frame.Tick };
                    act.Add(a.Sent);
                    a.SentNeutral = true;
                }
                continue;
            }
            uint period = a.Tier == Tier.Full ? 1u : 3u;
            if ((_tick + (uint)a.Slot) % period != 0)
                continue;
            if (a.Tier == Tier.Full)
                Stats.FullAgentTicks++;
            else
                Stats.ReducedAgentTicks++;
            float step = dt * period;
            Stats.GoalTicks[(int)a.Goal]++;
            ref EntityRecord body = ref _entities[a.EntityIndex];
            if (body.Vx * body.Vx + body.Vy * body.Vy < 0.04f)
                Stats.GoalStill[(int)a.Goal]++;
            try
            {
                RunSkill(a, step);
                RunMotor(a, step, act);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Fault(a, "skill", e);
            }
        }
        long t4 = Stopwatch.GetTimestamp();
        RecordFrame(frame);
        Stats.RecordMs += Stopwatch.GetElapsedTime(t4).TotalMilliseconds;

        Stats.Rays += _map.RaysCast + (_map.Mesh?.RaysCast ?? 0) - raysBefore;
        Stats.AddTick(
            Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds,
            Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds,
            Stopwatch.GetElapsedTime(t2, t3).TotalMilliseconds,
            Stopwatch.GetElapsedTime(t3, t4).TotalMilliseconds);
    }

    private void Ingest(ObserveFrame frame)
    {
        for (int i = 0; i < frame.EntityCount; i++)
        {
            ref EntityRecord record = ref frame.Entities[i];
            if (!_entityIndex.TryGetValue(record.Id, out int index))
            {
                index = _entityCount++;
                if (index == _entities.Length)
                {
                    Array.Resize(ref _entities, index * 2);
                    Array.Resize(ref _entitySeenTick, index * 2);
                    Array.Resize(ref _agentOfEntity, index * 2);
                    Array.Resize(ref _shotAt, index * 2);
                    Array.Resize(ref _bucketItems, index * 2);
                    Array.Resize(ref _near, index * 2);
                }
                _entityIndex[record.Id] = index;
                _agentOfEntity[index] = -1;
                _shotAt[index] = double.NegativeInfinity;
            }
            _entities[index] = record;
            _entitySeenTick[index] = frame.Tick;
        }

        for (int i = 0; i < frame.AgentCount; i++)
        {
            ref AgentRecord record = ref frame.Agents[i];
            Agent? agent = record.Slot < _agentBySlot.Length ? _agentBySlot[record.Slot] : null;
            if (agent is null || !_entityIndex.TryGetValue(record.Entity, out int index))
                continue;
            if (agent.EntityIndex != index)
            {
                if (agent.EntityIndex >= 0)
                    _agentOfEntity[agent.EntityIndex] = -1;
                agent.EntityIndex = index;
                agent.EntityId = record.Entity;
                _agentOfEntity[index] = _agents.IndexOf(agent);
            }
            agent.Ammo = record.AmmoInMag;
            agent.Reserve = record.AmmoReserve;
            bool alive = (_entities[index].Flags & EntityFlags.Alive) != 0;
            if (!alive && agent.Alive)
                OnAgentDied(agent);
            if (alive && !agent.Alive)
            {
                agent.ResetForSpawn();
                agent.Alive = true;
                OnAgentSpawned(agent);
            }
            agent.Alive = alive;
        }

        if (_observers.Length < frame.ObserverCount)
            _observers = new ObserverRecord[frame.ObserverCount];
        Array.Copy(frame.Observers, _observers, frame.ObserverCount);
        _observerCount = frame.ObserverCount;

        // Counting sort of living players into buckets.
        Array.Clear(_bucketStart);
        for (int i = 0; i < _entityCount; i++)
        {
            if (IsLivePlayer(i))
                _bucketStart[BucketOf(_entities[i].X, _entities[i].Y) + 1]++;
        }
        for (int b = 1; b < _bucketStart.Length; b++)
            _bucketStart[b] += _bucketStart[b - 1];
        Span<int> cursor = _bucketStart.Length <= 1024 ? stackalloc int[_bucketStart.Length] : new int[_bucketStart.Length];
        _bucketStart.CopyTo(cursor);
        for (int i = 0; i < _entityCount; i++)
        {
            if (IsLivePlayer(i))
                _bucketItems[cursor[BucketOf(_entities[i].X, _entities[i].Y)]++] = i;
        }
    }

    private bool IsLivePlayer(int index) =>
        _entitySeenTick[index] == _tick
        && _entities[index].Kind == EntityKind.Player
        && (_entities[index].Flags & EntityFlags.Alive) != 0;

    private int BucketOf(float x, float y)
    {
        int bx = Math.Clamp((int)((x - _map.Origin.X) / BucketSize), 0, _bucketsX - 1);
        int by = Math.Clamp((int)((y - _map.Origin.Y) / BucketSize), 0, _bucketsY - 1);
        return by * _bucketsX + bx;
    }

    /// <summary>Indices of living players within a radius, written to <see cref="_near"/>.</summary>
    private int QueryNear(Vector2 position, float radius)
    {
        int x0 = Math.Clamp((int)((position.X - radius - _map.Origin.X) / BucketSize), 0, _bucketsX - 1);
        int x1 = Math.Clamp((int)((position.X + radius - _map.Origin.X) / BucketSize), 0, _bucketsX - 1);
        int y0 = Math.Clamp((int)((position.Y - radius - _map.Origin.Y) / BucketSize), 0, _bucketsY - 1);
        int y1 = Math.Clamp((int)((position.Y + radius - _map.Origin.Y) / BucketSize), 0, _bucketsY - 1);
        float r2 = radius * radius;
        int count = 0;
        for (int by = y0; by <= y1; by++)
        {
            for (int bx = x0; bx <= x1; bx++)
            {
                int bucket = by * _bucketsX + bx;
                for (int k = _bucketStart[bucket]; k < _bucketStart[bucket + 1]; k++)
                {
                    int index = _bucketItems[k];
                    float dx = _entities[index].X - position.X, dy = _entities[index].Y - position.Y;
                    if (dx * dx + dy * dy <= r2)
                        _near[count++] = index;
                }
            }
        }
        return count;
    }

    /// <summary>
    /// Full fidelity for any agent a human could be looking at within a couple
    /// of seconds: close by, or in line of sight within range. Facing is
    /// ignored because a person can turn faster than a bot can change tier.
    /// </summary>
    private void UpdateFidelity()
    {
        foreach (Agent a in _agents)
        {
            if (a.EntityIndex < 0)
                continue;
            bool full = _options.ForceFullFidelity;
            Vector2 position = PositionOf(a.EntityIndex);
            Vector3 head = new(position, _entities[a.EntityIndex].Z + EyeHeight);
            for (int i = 0; i < _observerCount && !full; i++)
            {
                Vector3 eye = new(_observers[i].X, _observers[i].Y, _observers[i].Z);
                float d2 = Vector2.DistanceSquared(new Vector2(eye.X, eye.Y), position);
                full = _observers[i].Spectating == a.EntityId
                    || d2 < 30f * 30f
                    || (d2 < 150f * 150f && _map.LineOfSight(eye, head));
            }
            if (!full && a.Goal == Goal.Engage && _entityIndex.TryGetValue(a.TargetEntity, out int target))
                full = (_entities[target].Flags & EntityFlags.Human) != 0;
            if (full)
            {
                a.Tier = Tier.Full;
                a.TierHoldUntil = _now + 2;
            }
            else if (_now >= a.TierHoldUntil)
            {
                a.Tier = Tier.Reduced;
            }
        }
    }

    private Vector2 PositionOf(int entityIndex) => new(_entities[entityIndex].X, _entities[entityIndex].Y);

    internal static float WrapAngle(float a)
    {
        while (a > MathF.PI)
            a -= 2 * MathF.PI;
        while (a < -MathF.PI)
            a += 2 * MathF.PI;
        return a;
    }
}
