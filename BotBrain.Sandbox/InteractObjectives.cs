using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotBrain.Protocol;

namespace BotBrain.Sandbox;

/// <summary>One objective worked by hand, as written in an <c>--objectives</c> file.</summary>
internal sealed class InteractSpec
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("x")] public float X { get; set; }
    [JsonPropertyName("y")] public float Y { get; set; }
    [JsonPropertyName("z")] public float Z { get; set; }
    /// <summary>How close a player must stand to work it, metres.</summary>
    [JsonPropertyName("radius")] public float Radius { get; set; } = 2.5f;
    /// <summary>The team that owns it at the start (the defenders).</summary>
    [JsonPropertyName("owner")] public int Owner { get; set; }
    /// <summary>Objectives a team must hold, settled, before it can work this one.</summary>
    [JsonPropertyName("requires")] public List<int> Requires { get; set; } = [];
    /// <summary>Seconds of holding interact to start a claim, and to undo one that is running.</summary>
    [JsonPropertyName("work")] public float Work { get; set; } = 3f;
    [JsonPropertyName("undo")] public float Undo { get; set; } = 3f;
    /// <summary>Seconds a claim runs before it settles.</summary>
    [JsonPropertyName("hold")] public float Hold { get; set; } = 20f;
    /// <summary>Whether its first owners can claim it back once it is lost.</summary>
    [JsonPropertyName("retake")] public bool Retake { get; set; } = true;
    /// <summary>Settled by the attackers, the match is won (Sabotage's C).</summary>
    [JsonPropertyName("final")] public bool Final { get; set; }
    /// <summary>Opening, it locks the objectives it requires so they can no longer change hands (Sabotage locks A and B once C opens).</summary>
    [JsonPropertyName("locks_requires")] public bool LocksRequires { get; set; }
}

/// <summary>Spawn waves per team, as MAG runs them: the first so many seconds after the start, then one every so many.</summary>
internal sealed class WaveSpec
{
    [JsonPropertyName("first")] public List<float> First { get; set; } = [];
    [JsonPropertyName("every")] public List<float> Every { get; set; } = [];
}

internal sealed class InteractFile
{
    [JsonPropertyName("objectives")] public List<InteractSpec> Objectives { get; set; } = [];
    /// <summary>Optional: spawn waves for the mode (Sabotage: attackers' first at 35 s, defenders' at 20 s, then every 20 s).</summary>
    [JsonPropertyName("waves")] public WaveSpec? Waves { get; set; }
}

/// <summary>
/// Objectives worked by hand for the sandbox, the way MAG's Sabotage
/// works them: settled, the other side holds interact for a few seconds to
/// start a claim; a claim runs down while the owners can undo it by holding
/// interact; run down, it settles in the claimers' hands. The rules are this
/// file's, not the brain's: the brain only sees owners and progress.
/// </summary>
internal sealed class InteractObjectives
{
    private sealed class State(InteractSpec spec)
    {
        public readonly InteractSpec Spec = spec;
        public int Owner = spec.Owner;
        /// <summary>Who owned it before the running claim.</summary>
        public int Before = spec.Owner;
        public float Progress = 1f;
        public bool Locked, Done;
        public uint Worker;
        public float Worked;
        public int Claims, Undone;
        public float SettledAt = -1;
    }

    private readonly List<State> _states;
    private readonly HashSet<int> _opened = [];
    public float WonAt { get; private set; } = -1;

    public InteractObjectives(string path)
    {
        InteractFile file = JsonSerializer.Deserialize<InteractFile>(File.ReadAllText(path)) ?? new InteractFile();
        _states = [.. file.Objectives.Select(o => new State(o))];
        Waves = file.Waves;
    }

    /// <summary>The file's spawn waves, if it has any.</summary>
    public WaveSpec? Waves { get; }

    public int Count => _states.Count;

    /// <summary>Claims started and claims undone, over all objectives.</summary>
    public int Claims => _states.Sum(s => s.Claims);
    public int Undone => _states.Sum(s => s.Undone);

    /// <summary>The team holding an objective now, and its progress.</summary>
    public (int Owner, float Progress) StateOf(int id) => ById(id) is { } s ? (s.Owner, s.Progress) : (-1, 0);

    public IEnumerable<ObjectiveInfo> Infos() => _states.Select(s => new ObjectiveInfo
    {
        Id = s.Spec.Id,
        Primitive = "interact",
        X = s.Spec.X,
        Y = s.Spec.Y,
        Z = s.Spec.Z,
        Radius = s.Spec.Radius,
        OwnerTeam = s.Owner,
        Progress = s.Progress,
        Requires = [.. s.Spec.Requires],
        Locked = s.Locked,
    });

    private State? ById(int id) => _states.FirstOrDefault(s => s.Spec.Id == id);

    private bool Workable(State s, int team) =>
        s.Spec.Requires.All(r => ById(r) is { } q && q.Owner == team && q.Progress >= 1f);

    public void Step(List<SimPlayer> players, List<EventRecord> events, float now)
    {
        if (WonAt >= 0)
            return;
        foreach (State s in _states)
        {
            if (s.Done)
                continue;
            // A final objective opening locks what it needs, once.
            if (s.Spec.LocksRequires && !_opened.Contains(s.Spec.Id) && s.Spec.Requires.Count > 0 && Workable(s, 1 - s.Spec.Owner))
            {
                _opened.Add(s.Spec.Id);
                foreach (int r in s.Spec.Requires)
                {
                    if (ById(r) is { } q)
                    {
                        q.Locked = true;
                        events.Add(new EventRecord { Kind = EventKind.Objective, A = (uint)q.Spec.Id, B = (uint)q.Owner, C = (uint)ObjectiveFlags.Locked, Value = q.Progress });
                    }
                }
            }
            // Who is at it: the player already working, or the first with interact held, near, who may.
            SimPlayer? worker = null;
            foreach (SimPlayer p in players)
            {
                if (!p.Alive || (p.Held & Simulation.BitInteract) == 0 || Vector2.Distance(p.Position, new Vector2(s.Spec.X, s.Spec.Y)) > s.Spec.Radius
                    || MathF.Abs(s.Spec.Z - p.Z) > 3f || !MayWork(s, p.Team))
                    continue;
                if (worker is null || p.Id == s.Worker)
                    worker = p;
            }
            if (worker is null || worker.Id != s.Worker)
            {
                s.Worker = worker?.Id ?? 0;
                s.Worked = 0;
            }
            if (worker is not null)
            {
                s.Worked += Simulation.Dt;
                bool undoing = s.Progress < 1f;
                if (s.Worked >= (undoing ? s.Spec.Undo : s.Spec.Work))
                {
                    if (undoing)
                    {
                        s.Owner = s.Before;
                        s.Progress = 1f;
                        s.Undone++;
                    }
                    else
                    {
                        s.Before = s.Owner;
                        s.Owner = worker.Team;
                        s.Progress = 0f;
                        s.Claims++;
                    }
                    s.Worker = 0;
                    s.Worked = 0;
                    events.Add(new EventRecord { Kind = EventKind.Objective, A = (uint)s.Spec.Id, B = (uint)s.Owner, Value = s.Progress });
                }
            }
            if (s.Progress < 1f)
            {
                s.Progress = MathF.Min(1f, s.Progress + Simulation.Dt / MathF.Max(0.1f, s.Spec.Hold));
                if (s.Progress >= 1f)
                {
                    s.SettledAt = now;
                    events.Add(new EventRecord { Kind = EventKind.Objective, A = (uint)s.Spec.Id, B = (uint)s.Owner, Value = 1f });
                    if (s.Spec.Final && s.Owner != s.Spec.Owner)
                    {
                        s.Done = true;
                        WonAt = now;
                    }
                }
            }
        }
    }

    private bool MayWork(State s, byte team)
    {
        if (s.Locked || s.Done)
            return false;
        if (s.Progress < 1f)
            return s.Owner != team; // undo the other side's running claim
        if (s.Owner == team)
            return false;
        if (team == s.Spec.Owner && !s.Spec.Retake)
            return false;
        return Workable(s, team);
    }

    public string Summary()
    {
        StringBuilder s = new();
        s.Append("  objectives ");
        s.Append(string.Join("   ", _states.Select(q => $"{q.Spec.Name}: team {q.Owner}{(q.Progress < 1f ? $" claiming {q.Progress:P0}" : "")}{(q.Locked ? " locked" : "")}, {q.Claims} claims, {q.Undone} undone")));
        s.Append(WonAt >= 0 ? $"; attackers won at {WonAt:F0} s" : "; not won");
        return s.ToString();
    }
}
