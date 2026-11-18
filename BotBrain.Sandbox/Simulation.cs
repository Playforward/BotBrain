using System.Numerics;
using System.Text;
using BotBrain.Core;
using BotBrain.Maps;
using BotBrain.Protocol;

namespace BotBrain.Sandbox;

internal enum PlayerKind
{
    /// <summary>Driven by the brain.</summary>
    Bot,
    /// <summary>Stands in for a real person: wanders the map and counts as an observer.</summary>
    Human,
    /// <summary>Stands still. A target.</summary>
    Dummy,
}

internal sealed class SimPlayer
{
    public uint Id;
    public PlayerKind Kind;
    public int Slot = -1;
    public byte Team;
    public string Name = "";
    public Vector2? FixedSpawn;
    public float FixedYaw;

    public bool Alive;
    public Vector2 Position, Velocity;
    /// <summary>Height of the feet, and how fast they are falling.</summary>
    public float Z, FallSpeed;
    public float Yaw, Pitch, Health;
    public int Ammo, Reserve;
    public float ReloadTimer, FireCooldown, RespawnTimer, SinceDamage;
    public bool Sprinting, FiredThisTick;
    /// <summary>How spread the gun is, 0 (at rest) to 1; see <see cref="GunSpread"/>.</summary>
    public float Spread;
    /// <summary>On a map with a navigation mesh, the polygon of the floor it stands on (0: look it up).</summary>
    public long Polygon;

    // Controls, as last received.
    public float MoveX, MoveY, LookX, LookY;
    public uint Held;
    public uint LastActTick;

    // Wandering, for stand-in humans.
    public int WanderTarget = -1;
    public Vector2 WanderSteer;

    public int Kills, Deaths, Shots, Hits;
    public float Distance, AliveTime, StillTime, MaxYawStep;
}

internal sealed class ZoneState
{
    public int Id;
    public Vector2 Position;
    public float Radius;
    /// <summary>-1 is fully team 1's, +1 fully team 0's.</summary>
    public float Progress;
    public int Owner = 255;
    public int Captures;
}

/// <summary>
/// A small shooter that plays the part of a game. It knows nothing about
/// behaviour: it moves bodies from stick input, traces shots, and reports
/// what is true. This is also the first draft of the body kit (spec
/// section 10).
/// </summary>
internal sealed class Simulation
{
    public const int TickHz = 30;
    public const float Dt = 1f / TickHz;
    public const float Eye = 1.6f, BodyHeight = 1.8f, HeadHeight = 1.5f;
    public const int Magazine = 30, ReserveAmmo = 150;
    public const uint BitFire = 1, BitSprint = 2, BitReload = 4, BitInteract = 8;

    public static readonly LookSpec Look = new() { MaxTurnDegS = 220, MaxPitchDegS = 140, Deadzone = 0.1f, CurveExponent = 2 };
    public static readonly MoveSpec Move = new();

    private readonly List<SimPlayer> _bots = [];
    private readonly List<MapPoint>[] _spawns = [[], []];
    private readonly List<MapPoint> _interest;
    private readonly Dictionary<int, FlowField> _wanderFields = [];
    private readonly float[] _scoreCarry = new float[2];
    private Rng _rng;

    public readonly GridMap Map;
    public readonly List<SimPlayer> Players = [];
    public readonly List<ZoneState> Zones = [];
    public readonly List<EventRecord> Events = [];
    public readonly int[] Score = new int[2];
    public uint Tick;
    /// <summary>Spawn waves (seconds) per team; null spawns each player on its own timer, as before.</summary>
    public WaveSpec? Waves;
    private readonly float[] _nextWave = new float[8];
    private readonly bool[] _waveNow = new bool[8];
    /// <summary>
    /// The match is over: the final objective went (as MAG's Sabotage: the
    /// defenders' last points with it), five seconds ago. The sandbox stops
    /// there, so a recording ends where a real match would.
    /// </summary>
    public bool Over => Interacts is { WonAt: >= 0 } won && Tick / (float)TickHz > won.WonAt + 5f;

    /// <summary>Stand-in humans stay where they spawn instead of wandering (someone sitting in the match, not playing).</summary>
    public bool HumansSit;
    /// <summary>Objectives worked by hand, from an <c>--objectives</c> file; null for none.</summary>
    public InteractObjectives? Interacts;

    public Simulation(GridMap map, ulong seed)
    {
        Map = map;
        _rng = new Rng(seed ^ 0x5A17UL);
        foreach (MapPoint p in map.Points)
        {
            if (p.Kind == MapPointKind.Spawn && p.Team < 2)
                _spawns[p.Team].Add(p);
            else if (p.Kind == MapPointKind.Zone)
                Zones.Add(new ZoneState { Id = p.Id, Position = p.Position, Radius = p.Radius });
        }
        _interest = [.. map.PointsOf(MapPointKind.Interest)];
    }

    public int BotCount => _bots.Count;

    public SimPlayer AddPlayer(PlayerKind kind, byte team, Vector2? position = null, float yaw = 0)
    {
        SimPlayer p = new() { Id = (uint)Players.Count + 1, Kind = kind, Team = team, FixedSpawn = position, FixedYaw = yaw };
        if (kind == PlayerKind.Bot)
        {
            p.Slot = _bots.Count;
            _bots.Add(p);
        }
        Players.Add(p);
        if (Waves is null)
            Spawn(p);
        else
            p.RespawnTimer = 0; // waits for its team's first wave
        return p;
    }

    public Manifest BuildManifest() => new()
    {
        Title = "sandbox",
        Build = "0",
        BodyKind = "server_side",
        TickHz = TickHz,
        Truth = "full",
        LookOutput = "stick",
        ControlModes = { ["foot_shooter"] = new ControlModeSpec { Actions = ["fire", "sprint", "reload", "interact"], Look = Look, Move = Move } },
        Teams = new TeamsSpec { Ids = [0, 1], Hostile = [[0, 1]] },
        Items = [new ItemSpec { Id = 1, Class = "rifle", Fire = "auto", Mag = Magazine, ReloadS = 2.4f, RoundsPerS = 10, RangeM = [5, 60] }],
        Objectives = ["hold_zone", "interact", "score_race"],
        Map = new MapRef { Id = Map.Id, Hash = Map.Hash() },
    };

    public SeatsMessage BuildSeats() => new() { Slots = [.. _bots.Select(b => new SeatInfo { Slot = b.Slot, Team = b.Team })] };

    public ObjectivesMessage BuildObjectives() => new()
    {
        Objectives = [.. Zones.Select(z => new ObjectiveInfo { Id = z.Id, Primitive = "hold_zone", X = z.Position.X, Y = z.Position.Y, Radius = z.Radius, OwnerTeam = z.Owner }),
            .. Interacts?.Infos() ?? []],
    };

    public void SetName(int slot, string name)
    {
        if (slot >= 0 && slot < _bots.Count)
            _bots[slot].Name = name;
    }

    public void ApplyAct(ActFrame act)
    {
        for (int i = 0; i < act.Count; i++)
        {
            ref ActRecord r = ref act.Records[i];
            if (r.Slot >= _bots.Count)
                continue;
            SimPlayer p = _bots[r.Slot];
            p.MoveX = r.Axis0 / 32767f;
            p.MoveY = r.Axis1 / 32767f;
            p.LookX = r.Axis2 / 32767f;
            p.LookY = r.Axis3 / 32767f;
            p.Held = r.Held;
            p.LastActTick = r.FromTick;
        }
    }

    public void Step()
    {
        Events.Clear();
        Tick++;
        if (Waves is not null)
        {
            float now = Tick * Dt;
            for (int team = 0; team < _waveNow.Length; team++)
            {
                if (Tick == 1)
                    _nextWave[team] = Waves.First.Count > team ? Waves.First[team] : 0;
                _waveNow[team] = now >= _nextWave[team];
                if (_waveNow[team])
                    _nextWave[team] += Waves.Every.Count > team ? MathF.Max(1f, Waves.Every[team]) : 10f;
            }
        }
        foreach (SimPlayer p in Players)
        {
            p.FiredThisTick = false;
            if (!p.Alive)
            {
                p.RespawnTimer -= Dt;
                // With waves, a player ready to go waits for its team's next one.
                if (p.RespawnTimer <= 0 && (Waves is null || _waveNow[Math.Min((int)p.Team, _waveNow.Length - 1)]))
                    Spawn(p);
                continue;
            }
            if (p.Kind == PlayerKind.Human && !HumansSit)
                Wander(p);
            else if (p.Kind == PlayerKind.Human)
                p.MoveX = p.MoveY = p.LookX = p.LookY = 0;
            else if (p.Kind == PlayerKind.Dummy)
                p.MoveX = p.MoveY = p.LookX = p.LookY = 0;
            Turn(p);
            Walk(p);
            Weapon(p);
            p.SinceDamage += Dt;
            if (p.SinceDamage > 5f && p.Health < 100f)
                p.Health = MathF.Min(100f, p.Health + 20f * Dt);
            p.AliveTime += Dt;
            if (p.Velocity.LengthSquared() < 0.04f)
                p.StillTime += Dt;
        }
        UpdateZones();
        Interacts?.Step(Players, Events, Tick / (float)TickHz);
    }

    private void Spawn(SimPlayer p)
    {
        if (p.FixedSpawn is { } fixedPosition)
        {
            p.Position = fixedPosition;
            p.Yaw = p.FixedYaw;
        }
        else
        {
            List<MapPoint> spawns = _spawns[Math.Min((int)p.Team, 1)];
            Vector2 at = spawns[_rng.NextInt(spawns.Count)].Position;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                Vector2 candidate = at + new Vector2(_rng.Range(-2f, 2f), _rng.Range(-2f, 2f));
                if (!Map.Collides(candidate.X, candidate.Y))
                {
                    at = candidate;
                    break;
                }
            }
            p.Position = at;
            p.Yaw = p.Team == 0 ? 0 : MathF.PI;
        }
        p.Velocity = default;
        p.Z = Map.GroundAt(p.Position);
        p.Polygon = 0;
        if (Map.Nav is { } nav && nav.Snap(new Vector3(p.Position, p.Z), 4f, out Vector3 floor))
        {
            p.Position = new Vector2(floor.X, floor.Y);
            p.Z = floor.Z;
        }
        p.FallSpeed = 0;
        p.Pitch = 0;
        p.Health = 100;
        p.Ammo = Magazine;
        p.Reserve = ReserveAmmo;
        p.ReloadTimer = p.FireCooldown = 0;
        p.SinceDamage = 100;
        p.Alive = true;
        p.MoveX = p.MoveY = p.LookX = p.LookY = 0;
        p.Held = 0;
        p.WanderTarget = -1;
    }

    private static float Wrap(float a)
    {
        while (a > MathF.PI)
            a -= 2 * MathF.PI;
        while (a < -MathF.PI)
            a += 2 * MathF.PI;
        return a;
    }

    private static float Curve(float stick)
    {
        float a = MathF.Abs(stick);
        if (a <= Look.Deadzone)
            return 0;
        float r = MathF.Min(1f, (a - Look.Deadzone) / (1f - Look.Deadzone));
        return MathF.Sign(stick) * MathF.Pow(r, Look.CurveExponent);
    }

    private static void Turn(SimPlayer p)
    {
        float step = -Curve(p.LookX) * Look.MaxTurnDegS * MathF.PI / 180f * Dt;
        p.MaxYawStep = MathF.Max(p.MaxYawStep, MathF.Abs(step));
        p.Yaw = Wrap(p.Yaw + step);
        p.Pitch = Math.Clamp(p.Pitch + Curve(p.LookY) * Look.MaxPitchDegS * MathF.PI / 180f * Dt, -1.4f, 1.4f);
    }

    private void Walk(SimPlayer p)
    {
        Vector2 forward = new(MathF.Cos(p.Yaw), MathF.Sin(p.Yaw));
        Vector2 right = new(forward.Y, -forward.X);
        Vector2 stick = new(p.MoveX, p.MoveY);
        float magnitude = MathF.Min(1f, stick.Length());
        Vector2 wanted = default;
        p.Sprinting = false;
        if (magnitude >= 0.15f)
        {
            p.Sprinting = (p.Held & BitSprint) != 0 && p.MoveY > 0.7f && magnitude > 0.8f && p.ReloadTimer <= 0 && (p.Held & BitFire) == 0;
            float speed = p.Sprinting ? Move.SprintMS : Move.WalkMS;
            wanted = (right * stick.X + forward * stick.Y) / stick.Length() * magnitude * speed;
        }
        Vector2 change = wanted - p.Velocity;
        float limit = 40f * Dt;
        if (change.Length() > limit)
            change *= limit / change.Length();
        p.Velocity += change;

        Vector2 wantedPosition = p.Position + p.Velocity * Dt;
        if (Map.Nav is { } nav)
        {
            // On the navigation mesh: along the floor the body is on (stairs and upper floors
            // included), stopped by walls and edges; at an edge with a drop link going this way, down it.
            Vector3 moved = nav.Move(new Vector3(p.Position, p.Z), new Vector3(wantedPosition, p.Z), ref p.Polygon);
            Vector2 flat = new(moved.X, moved.Y);
            // Off a drop only when really stopped at the edge (not sliding along it).
            bool stopped = Vector2.Distance(flat, p.Position) < 0.5f * Vector2.Distance(wantedPosition, p.Position);
            if (stopped && p.Velocity.LengthSquared() > 0.25f && nav.DropAt(moved, p.Velocity, out Vector3 landing))
            {
                moved = landing;
                flat = new Vector2(landing.X, landing.Y);
                p.Polygon = 0;
            }
            else if (Vector2.DistanceSquared(flat, wantedPosition) > 1e-4f)
                p.Velocity = (flat - p.Position) / Dt; // pressed against a wall: what is left of the step
            p.Distance += Vector2.Distance(flat, p.Position);
            p.Position = flat;
            p.Z = moved.Z;
            p.FallSpeed = 0;
            return;
        }
        Vector2 next = Map.Move(p.Position, wantedPosition);
        if (next.X != wantedPosition.X)
            p.Velocity.X = 0;
        if (next.Y != wantedPosition.Y)
            p.Velocity.Y = 0;
        p.Distance += Vector2.Distance(next, p.Position);
        p.Position = next;

        // Feet follow the ground; off a ledge they fall to it.
        float floor = Map.GroundAt(next);
        if (floor < p.Z - 0.05f)
        {
            p.FallSpeed += 9.81f * Dt;
            p.Z = MathF.Max(floor, p.Z - p.FallSpeed * Dt);
        }
        else
        {
            p.Z = floor;
            p.FallSpeed = 0;
        }
    }

    private void Weapon(SimPlayer p)
    {
        p.FireCooldown -= Dt;
        p.Spread = GunSpread.Rifle.Settle(p.Spread, p.Velocity.Length() / Move.WalkMS, Dt);
        if (p.ReloadTimer > 0)
        {
            p.ReloadTimer -= Dt;
            if (p.ReloadTimer <= 0)
            {
                int take = Math.Min(Magazine - p.Ammo, p.Reserve);
                p.Ammo += take;
                p.Reserve -= take;
            }
            return;
        }
        if ((p.Held & BitReload) != 0 && p.Ammo < Magazine && p.Reserve > 0)
        {
            p.ReloadTimer = 2.4f;
            return;
        }
        if ((p.Held & BitFire) != 0 && p.FireCooldown <= 0 && p.Ammo > 0 && !p.Sprinting)
            Fire(p);
    }

    private void Fire(SimPlayer p)
    {
        p.Ammo--;
        p.FireCooldown = 0.1f;
        p.Shots++;
        p.FiredThisTick = true;
        Vector2 off = GunSpread.Rifle.Scatter(p.Spread, _rng.NextFloat(), _rng.NextFloat());
        p.Spread = MathF.Min(1f, p.Spread + GunSpread.Rifle.PerShot);
        float yaw = p.Yaw + off.X, pitch = p.Pitch + off.Y;
        Vector2 direction = new(MathF.Cos(yaw), MathF.Sin(yaw));
        float slope = MathF.Tan(pitch);
        float reach;
        if (Map.Mesh is { } mesh)
        {
            // The level's own collision stops the bullet, in three dimensions.
            Vector3 aim = Vector3.Normalize(new Vector3(direction, slope));
            float hitAt = mesh.Raycast(new Vector3(p.Position, p.Z + Eye), aim, 150f);
            reach = hitAt * MathF.Sqrt(aim.X * aim.X + aim.Y * aim.Y);
        }
        else
        {
            reach = Map.Raycast(p.Position, direction, 150f);
        }
        SimPlayer? hit = null;
        float hitHeight = 0;
        foreach (SimPlayer q in Players)
        {
            if (!q.Alive || q.Team == p.Team)
                continue;
            Vector2 d = q.Position - p.Position;
            float along = Vector2.Dot(d, direction);
            if (along <= 0 || along >= reach || d.LengthSquared() - along * along > GridMap.AgentRadius * GridMap.AgentRadius)
                continue;
            float height = p.Z + Eye + slope * along - q.Z;
            if (height < 0 || height > BodyHeight)
                continue;
            reach = along;
            hit = q;
            hitHeight = height;
        }
        Events.Add(new EventRecord { Kind = EventKind.Shot, A = p.Id, X = p.Position.X, Y = p.Position.Y, Z = p.Z + Eye });
        if (hit is not null)
        {
            p.Hits++;
            Damage(hit, p, hitHeight > HeadHeight ? 50f : 25f);
        }
        // Recoil: the muzzle climbs and wanders, and the player has to pull it back.
        p.Pitch += 0.006f;
        p.Yaw += _rng.Gaussian() * 0.003f;
    }

    private void Damage(SimPlayer victim, SimPlayer attacker, float amount)
    {
        victim.Health -= amount;
        victim.SinceDamage = 0;
        Vector2 toward = attacker.Position - victim.Position;
        if (toward.LengthSquared() > 1e-6f)
            toward = Vector2.Normalize(toward);
        Events.Add(new EventRecord { Kind = EventKind.Damage, A = victim.Id, B = attacker.Id, X = toward.X, Y = toward.Y, Value = amount });
        if (victim.Health > 0)
            return;
        victim.Alive = false;
        victim.RespawnTimer = 5f;
        victim.Deaths++;
        attacker.Kills++;
        Events.Add(new EventRecord { Kind = EventKind.Death, A = victim.Id, B = attacker.Id, C = 1 });
    }

    private void UpdateZones()
    {
        foreach (ZoneState zone in Zones)
        {
            int team0 = 0, team1 = 0;
            foreach (SimPlayer p in Players)
            {
                if (!p.Alive || Vector2.DistanceSquared(p.Position, zone.Position) > zone.Radius * zone.Radius)
                    continue;
                if (p.Team == 0)
                    team0++;
                else
                    team1++;
            }
            if (team0 > 0 && team1 == 0)
                zone.Progress = MathF.Min(1f, zone.Progress + Math.Min(team0, 3) * Dt / 6f);
            else if (team1 > 0 && team0 == 0)
                zone.Progress = MathF.Max(-1f, zone.Progress - Math.Min(team1, 3) * Dt / 6f);
            int owner = zone.Progress >= 1f ? 0 : zone.Progress <= -1f ? 1 : zone.Owner;
            if (owner != zone.Owner)
            {
                zone.Owner = owner;
                zone.Captures++;
                Events.Add(new EventRecord { Kind = EventKind.Objective, A = (uint)zone.Id, B = (uint)owner, Value = zone.Progress });
            }
            if (zone.Owner < 2)
            {
                _scoreCarry[zone.Owner] += Dt;
                if (_scoreCarry[zone.Owner] >= 1f)
                {
                    _scoreCarry[zone.Owner] -= 1f;
                    Score[zone.Owner]++;
                }
            }
        }
    }

    /// <summary>A stand-in person: walks from one point of interest to the next.</summary>
    private void Wander(SimPlayer p)
    {
        if (_interest.Count == 0)
            return;
        if (p.WanderTarget < 0 || Vector2.DistanceSquared(p.Position, _interest[p.WanderTarget].Position) < 16f)
        {
            p.WanderTarget = _rng.NextInt(_interest.Count);
            p.WanderSteer = p.Position;
        }
        if (!_wanderFields.TryGetValue(p.WanderTarget, out FlowField? field))
            _wanderFields[p.WanderTarget] = field = Map.BuildFlowField(_interest[p.WanderTarget].Position);
        if (Tick % 6 == 0 || Vector2.DistanceSquared(p.Position, p.WanderSteer) < 1f)
            p.WanderSteer = field.SteerPoint(p.Position);
        Vector2 to = p.WanderSteer - p.Position;
        if (to.LengthSquared() < 0.01f)
        {
            p.MoveX = p.MoveY = 0;
            return;
        }
        float error = Wrap(MathF.Atan2(to.Y, to.X) - p.Yaw);
        p.Yaw = Wrap(p.Yaw + Math.Clamp(error, -3f * Dt, 3f * Dt));
        p.MoveX = 0;
        p.MoveY = MathF.Abs(error) < 0.6f ? 1f : 0.3f;
        p.LookX = p.LookY = 0;
    }

    public void FillObserve(ObserveFrame frame)
    {
        frame.Tick = Tick;
        frame.TimeMs = (uint)Math.Round(Tick * 1000.0 / TickHz);
        if (frame.Entities.Length < Players.Count)
            frame.Entities = new EntityRecord[Players.Count];
        if (frame.Agents.Length < _bots.Count)
            frame.Agents = new AgentRecord[_bots.Count];
        frame.EntityCount = frame.AgentCount = frame.ObserverCount = 0;
        foreach (SimPlayer p in Players)
        {
            EntityFlags flags = EntityFlags.None;
            if (p.Alive)
                flags |= EntityFlags.Alive;
            if (p.Kind == PlayerKind.Human)
                flags |= EntityFlags.Human;
            if (p.FiredThisTick)
                flags |= EntityFlags.Firing;
            if (p.Sprinting)
                flags |= EntityFlags.Sprinting;
            frame.Entities[frame.EntityCount++] = new EntityRecord
            {
                Id = p.Id,
                Kind = EntityKind.Player,
                Team = p.Team,
                Flags = flags,
                X = p.Position.X,
                Y = p.Position.Y,
                Z = p.Z,
                Vx = p.Velocity.X,
                Vy = p.Velocity.Y,
                Yaw = p.Yaw,
                Pitch = p.Pitch,
                HeldItem = 1,
                Health = (byte)Math.Clamp(p.Health * 2.54f, 0, 254),
            };
            if (p.Kind == PlayerKind.Bot)
            {
                frame.Agents[frame.AgentCount++] = new AgentRecord
                {
                    Slot = (ushort)p.Slot,
                    Entity = p.Id,
                    ControlMode = ControlMode.FootShooter,
                    AmmoInMag = (ushort)p.Ammo,
                    AmmoReserve = (ushort)p.Reserve,
                };
            }
            else if (p.Kind == PlayerKind.Human)
            {
                if (frame.ObserverCount == frame.Observers.Length)
                    Array.Resize(ref frame.Observers, frame.ObserverCount * 2);
                frame.Observers[frame.ObserverCount++] = new ObserverRecord
                {
                    Entity = p.Id,
                    X = p.Position.X,
                    Y = p.Position.Y,
                    Z = p.Z + Eye,
                    Yaw = p.Yaw,
                    Pitch = p.Pitch,
                    Fov = 1.4f,
                };
            }
        }
    }

    public void FillEvents(EventBatch batch)
    {
        batch.Tick = Tick;
        batch.Count = 0;
        foreach (EventRecord e in Events)
            batch.Add(e);
    }

    /// <summary>What happened in the match, for checking the bots did something sensible.</summary>
    public string Summary()
    {
        int kills = 0, shots = 0, hits = 0;
        float distance = 0, alive = 0, still = 0, maxYawStep = 0;
        foreach (SimPlayer p in _bots)
        {
            kills += p.Kills;
            shots += p.Shots;
            hits += p.Hits;
            distance += p.Distance;
            alive += p.AliveTime;
            still += p.StillTime;
            maxYawStep = MathF.Max(maxYawStep, p.MaxYawStep);
        }
        StringBuilder s = new();
        s.AppendLine($"match: {Tick} ticks ({Tick / (float)TickHz:F0} s), {_bots.Count} bots, {Players.Count - _bots.Count} others");
        s.AppendLine($"  score      team 0: {Score[0]}   team 1: {Score[1]}   zone captures: {Zones.Sum(z => z.Captures)}");
        s.AppendLine($"  combat     {kills} kills, {shots} shots, {hits} hits ({(shots > 0 ? 100f * hits / shots : 0):F1}% accuracy)");
        s.AppendLine($"  movement   {(alive > 0 ? distance / alive : 0):F2} m/s average while alive, standing still {(alive > 0 ? 100f * still / alive : 0):F0}% of the time");
        if (Interacts is not null)
            s.AppendLine(Interacts.Summary());
        s.Append($"  turning    fastest turn in one tick {maxYawStep * 180f / MathF.PI:F2} degrees (limit {Look.MaxTurnDegS * Dt:F2})");
        return s.ToString();
    }

    /// <summary>A number that changes if anything about the match's outcome changes.</summary>
    public ulong Fingerprint()
    {
        ulong h = 1469598103934665603UL;
        void Mix(ulong v) => h = (h ^ v) * 1099511628211UL;
        foreach (SimPlayer p in Players)
        {
            Mix(BitConverter.SingleToUInt32Bits(p.Position.X));
            Mix(BitConverter.SingleToUInt32Bits(p.Position.Y));
            Mix(BitConverter.SingleToUInt32Bits(p.Yaw));
            Mix((ulong)p.Kills);
            Mix((ulong)p.Shots);
        }
        Mix((ulong)Score[0]);
        Mix((ulong)Score[1]);
        return h;
    }
}
