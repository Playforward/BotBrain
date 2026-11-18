using System.Text;

namespace BotBrain.Core;

/// <summary>Where the brain's time goes, per world.</summary>
public sealed class BrainStats
{
    private readonly List<float> _tickMs = new(16384);
    private double _worstTotal;
    private string _worst = "";

    public double IngestMs, PerceptionMs, DecisionMs, MotorMs, CodecMs, EventsMs, FlowFieldMs, RecordMs;
    public long Rays, PathRequests, PathFailures, Alerts, Noticed, StuckEvents, FullAgentTicks, ReducedAgentTicks;
    /// <summary>Hunts started, by reason (index = HuntReason), and how many of them went round the side.</summary>
    public readonly long[] Hunts = new long[3];
    public long Flanks, Camps, AgentsLeft;
    /// <summary>Agent updates that threw: the agent is reset and the world carries on (see World.Tick).</summary>
    public long Faults;

    /// <summary>Agent updates spent on each goal, and how many of those the agent was standing still.</summary>
    public readonly long[] GoalTicks = new long[9], GoalStill = new long[9], GoalStuck = new long[9];

    /// <summary>Roles given (index = Role: none, seeker, defender, attacker), objective changes heard, objectives switched (and of those, because the one played was lost or won), hands put to work, and refusals given up on.</summary>
    public readonly long[] Roles = new long[4];
    public long SiteChanges, SiteSwitches, SiteLosses, Interacts, Refusals;

    public int Ticks => _tickMs.Count;

    internal void AddTick(double ingest, double perception, double decision, double motor)
    {
        IngestMs += ingest;
        PerceptionMs += perception;
        DecisionMs += decision;
        MotorMs += motor;
        double total = ingest + perception + decision + motor;
        if (total > _worstTotal)
        {
            _worstTotal = total;
            _worst = $"ingest {ingest:F2}, perception {perception:F2}, decision {decision:F2}, skills+motor {motor:F2}";
        }
        _tickMs.Add((float)total);
    }

    /// <summary>Mean cost of everything the brain does for one tick, in milliseconds.</summary>
    public double MeanMs => Ticks == 0 ? 0 : (IngestMs + PerceptionMs + DecisionMs + MotorMs + CodecMs + EventsMs) / Ticks;

    public double Percentile(double p)
    {
        if (_tickMs.Count == 0)
            return 0;
        float[] sorted = [.. _tickMs];
        Array.Sort(sorted);
        return sorted[Math.Clamp((int)(p * sorted.Length), 0, sorted.Length - 1)];
    }

    /// <summary>Share of one CPU core the brain needs to keep up at a tick rate.</summary>
    public double CoreShare(int tickHz) => MeanMs * tickHz / 1000.0;

    public string Report(int agents, int tickHz)
    {
        StringBuilder s = new();
        double n = Math.Max(1, Ticks);
        double seconds = n / tickHz;
        s.AppendLine($"brain: {agents} agents, {Ticks} ticks ({seconds:F0} s of match at {tickHz} Hz)");
        int worst = 0, over = 0;
        for (int i = 0; i < _tickMs.Count; i++)
        {
            if (_tickMs[i] > _tickMs[worst])
                worst = i;
            if (_tickMs[i] > 5f)
                over++;
        }
        s.AppendLine($"  per tick   mean {MeanMs:F3} ms   p50 {Percentile(0.5):F3}   p99 {Percentile(0.99):F3}   max {Percentile(1.0):F3} (tick {worst + 1}; {over} ticks over 5 ms)");
        s.AppendLine($"  worst tick {_worst}");
        s.AppendLine($"  one core   {CoreShare(tickHz) * 100:F1}% at {tickHz} Hz (tick budget {1000.0 / tickHz:F1} ms)");
        s.AppendLine($"  phases     ingest {IngestMs / n:F3}  perception {PerceptionMs / n:F3}  decision {DecisionMs / n:F3}  skills+motor {MotorMs / n:F3}  codec {CodecMs / n:F3}  events {EventsMs / n:F3}  (ms per tick)");
        if (RecordMs > 0)
            s.AppendLine($"  recording  {RecordMs / n:F3} ms per tick, not counted above");
        s.AppendLine($"  fidelity   full {FullAgentTicks / n:F1}  reduced {ReducedAgentTicks / n:F1}  (agent updates per tick)");
        s.AppendLine($"  rays       {Rays / seconds:F0} per second");
        s.AppendLine($"  paths      {PathRequests} requested, {PathFailures} failed; flow fields built in {FlowFieldMs:F0} ms");
        s.AppendLine($"  belief     {Noticed} enemies noticed, {Alerts} alerts from hits and gunfire, {StuckEvents} stuck recoveries ("
            + string.Join(", ", GoalStuck.Select((c, i) => c == 0 ? null : $"{c} {(Goal)i}").Where(x => x is not null)) + ")");
        s.AppendLine($"  hunting    {Hunts[1]} revenge, {Hunts[2]} toward gunfire; {Flanks} of them round the side; {Camps} camps");
        if (Roles.Sum() > 0)
            s.AppendLine($"  objectives {Roles[1]} seekers, {Roles[2]} defenders, {Roles[3]} attackers; {SiteChanges} changes, {SiteSwitches} switches ({SiteLosses} after a loss or win), {Interacts} times at work, {Refusals} refused");
        if (Faults > 0)
            s.AppendLine($"  FAULTS     {Faults} agent updates threw and were reset (a bug: read the link log)");
        string[] goals = ["none", "engage", "investigate", "go to zone", "hold", "roam", "hunt", "camp", "objective"];
        long all = Math.Max(1, GoalTicks.Sum());
        s.Append("  goals      " + string.Join("  ", goals.Select((g, i) => GoalTicks[i] == 0 ? null
            : $"{g} {100.0 * GoalTicks[i] / all:F0}% (still {100.0 * GoalStill[i] / GoalTicks[i]:F0}%)").Where(x => x is not null)));
        return s.ToString();
    }
}
