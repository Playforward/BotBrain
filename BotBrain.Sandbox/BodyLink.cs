using System.Diagnostics;
using System.Net.Sockets;
using BotBrain.Protocol;

namespace BotBrain.Sandbox;

internal sealed class RunResult
{
    public int Ticks;
    public double SimMsPerTick;
    public double WallSeconds;
    /// <summary>Ticks where the controls applied were older than the previous tick's (real-time mode only).</summary>
    public int LateTicks;
    public int FirstLateTick = -1, LastLateTick = -1;
    public int SeatsTaken;
    public Welcome? Welcome;
}

/// <summary>
/// The sandbox's side of the attach protocol: a server-side body with one
/// link carrying every bot.
/// </summary>
internal sealed class BodyLink(string host, int port)
{
    private readonly Lock _gate = new();
    private readonly ManualResetEventSlim _answered = new(false);
    private uint _answeredTick = uint.MaxValue;
    private volatile bool _closed;

    /// <param name="lockstep">
    /// Wait for the brain's answer to each tick before simulating the next.
    /// Repeatable and as fast as both sides can go. A real game never does
    /// this (spec rule 4); with it off the sandbox runs at 30 Hz by the clock
    /// and uses whatever controls have arrived.
    /// </param>
    public RunResult Run(Simulation sim, int ticks, bool lockstep)
    {
        using TcpClient client = new() { NoDelay = true };
        client.Connect(host, port);
        using FrameStream frames = new(client.GetStream());
        RunResult result = new();

        frames.WriteJson(MessageType.Hello, sim.BuildManifest());
        if (!frames.Read(out MessageType type, out ReadOnlyMemory<byte> payload) || type != MessageType.Welcome)
            throw new InvalidOperationException("the brain did not answer HELLO with WELCOME");
        result.Welcome = Json.Decode<Welcome>(payload.Span);

        frames.WriteJson(MessageType.Seats, sim.BuildSeats());
        if (result.Welcome.MapLoaded)
        {
            for (int i = 0; i < sim.BotCount; i++)
            {
                if (!frames.Read(out type, out payload) || type != MessageType.SeatTake)
                    throw new InvalidOperationException("expected SEAT_TAKE");
                SeatTake take = Json.Decode<SeatTake>(payload.Span);
                sim.SetName(take.Slot, take.Name);
                result.SeatsTaken++;
            }
        }
        frames.WriteJson(MessageType.Objectives, sim.BuildObjectives());
        frames.WriteJson(MessageType.Phase, new PhaseMessage { Phase = "live" });

        Thread reader = new(() => ReadLoop(frames, sim)) { IsBackground = true, Name = "body-reader" };
        reader.Start();

        ObserveFrame observe = new();
        EventBatch events = new();
        byte[] buffer = new byte[64 * 1024];
        long simTicks = 0;
        Stopwatch wall = Stopwatch.StartNew();
        for (int t = 0; t < ticks && !_closed && !sim.Over; t++)
        {
            long start = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                if (!lockstep && t > 1)
                {
                    foreach (SimPlayer p in sim.Players)
                    {
                        if (p.Kind == PlayerKind.Bot && p.Alive && p.LastActTick + 4 < sim.Tick)
                        {
                            result.LateTicks++;
                            if (result.FirstLateTick < 0)
                                result.FirstLateTick = t;
                            result.LastLateTick = t;
                            break;
                        }
                    }
                }
                sim.Step();
                sim.FillObserve(observe);
                sim.FillEvents(events);
            }
            simTicks += Stopwatch.GetTimestamp() - start;

            // Encode first: it may replace the buffer with a larger one.
            if (events.Count > 0)
            {
                int eventLength = events.Encode(ref buffer);
                frames.Write(MessageType.Event, buffer.AsSpan(0, eventLength));
            }
            _answered.Reset();
            int observeLength = observe.Encode(ref buffer);
            frames.Write(MessageType.Observe, buffer.AsSpan(0, observeLength));

            if (lockstep)
            {
                while (Volatile.Read(ref _answeredTick) != observe.Tick)
                {
                    if (!_answered.Wait(10000) || _closed)
                        throw new TimeoutException($"no ACT for tick {observe.Tick}");
                    _answered.Reset();
                }
            }
            else
            {
                double due = (t + 1) * 1000.0 / Simulation.TickHz;
                double wait = due - wall.Elapsed.TotalMilliseconds;
                if (wait > 0)
                    Thread.Sleep(TimeSpan.FromMilliseconds(wait));
            }
            result.Ticks++;
        }
        result.WallSeconds = wall.Elapsed.TotalSeconds;
        result.SimMsPerTick = Stopwatch.GetElapsedTime(0, simTicks).TotalMilliseconds / Math.Max(1, result.Ticks);
        frames.Write(MessageType.Bye, []);
        client.Client.Shutdown(SocketShutdown.Send);
        reader.Join(2000);
        return result;
    }

    private void ReadLoop(FrameStream frames, Simulation sim)
    {
        ActFrame act = new();
        try
        {
            while (frames.Read(out MessageType type, out ReadOnlyMemory<byte> payload))
            {
                if (type != MessageType.Act)
                    continue;
                act.Decode(payload.Span);
                lock (_gate)
                    sim.ApplyAct(act);
                Volatile.Write(ref _answeredTick, act.Tick);
                _answered.Set();
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        _closed = true;
        _answered.Set();
    }
}
