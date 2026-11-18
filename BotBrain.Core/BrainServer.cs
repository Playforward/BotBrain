using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using BotBrain.Maps;
using BotBrain.Protocol;

namespace BotBrain.Core;

/// <summary>
/// Listens for bodies. Each link gets its own thread and, in this build, its
/// own world: joining several links into one match is not implemented yet.
/// </summary>
public sealed class BrainServer(IMapStore maps, BrainOptions options) : IDisposable
{
    private TcpListener? _listener;
    private int _nextWorld;

    /// <summary>Raised on the link's thread when its body goes away.</summary>
    public event Action<World>? WorldClosed;

    /// <summary>Raised for anything worth a log line.</summary>
    public event Action<string>? Log;

    /// <summary>Starts listening and returns the port (useful when 0 was asked for).</summary>
    public int Start(IPAddress address, int port)
    {
        _listener = new TcpListener(address, port);
        _listener.Start();
        new Thread(AcceptLoop) { IsBackground = true, Name = "brain-accept" }.Start();
        return ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    private void AcceptLoop()
    {
        try
        {
            while (true)
            {
                TcpClient client = _listener!.AcceptTcpClient();
                client.NoDelay = true;
                new Thread(() => RunLink(client)) { IsBackground = true, Name = "brain-link" }.Start();
            }
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void RunLink(TcpClient client)
    {
        World? world = null;
        try
        {
            using FrameStream frames = new(client.GetStream());
            ObserveFrame observe = new();
            ActFrame act = new();
            EventBatch events = new();
            byte[] buffer = new byte[64 * 1024];
            long faultsLogged = 0;
            while (frames.Read(out MessageType type, out ReadOnlyMemory<byte> payload))
            {
                switch (type)
                {
                    case MessageType.Hello:
                        world = Open(frames, Json.Decode<Manifest>(payload.Span));
                        break;
                    case MessageType.Seats:
                        foreach (SeatInfo seat in Json.Decode<SeatsMessage>(payload.Span).Slots)
                        {
                            if (world is null)
                                continue;
                            // No population director yet: every seat on offer is taken at once.
                            Persona persona = world.AddAgent(seat.Slot, seat.Team);
                            frames.WriteJson(MessageType.SeatTake, new SeatTake { Slot = seat.Slot, Name = persona.Name, AccountId = $"bot-{persona.Seed:x}" });
                        }
                        break;
                    case MessageType.SeatLeave:
                        world?.RemoveAgent(Json.Decode<SeatLeave>(payload.Span).Slot);
                        break;
                    case MessageType.Objectives:
                        world?.SetObjectives(Json.Decode<ObjectivesMessage>(payload.Span));
                        if (world?.SiteReport is { } sites)
                            Log?.Invoke($"world {world.Id}: {sites}");
                        break;
                    case MessageType.Event:
                    {
                        long start = Stopwatch.GetTimestamp();
                        events.Decode(payload.Span);
                        world?.OnEvents(events);
                        if (world is not null)
                            world.Stats.EventsMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                        break;
                    }
                    case MessageType.Observe:
                    {
                        long start = Stopwatch.GetTimestamp();
                        observe.Decode(payload.Span);
                        double decode = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                        act.Tick = observe.Tick;
                        act.Count = 0;
                        act.CommandCount = 0;
                        world?.Tick(observe, act);
                        if (world is not null && world.Stats.Faults != faultsLogged)
                        {
                            // 1, 2, 4, 8 ... so a fault every tick cannot flood the log.
                            if ((world.Stats.Faults & (world.Stats.Faults - 1)) == 0)
                                Log?.Invoke($"world {world.Id}: agent fault #{world.Stats.Faults} (agent reset, match goes on): {world.LastFault}");
                            faultsLogged = world.Stats.Faults;
                        }
                        start = Stopwatch.GetTimestamp();
                        int length = act.Encode(ref buffer);
                        frames.Write(MessageType.Act, buffer.AsSpan(0, length));
                        if (world is not null)
                            world.Stats.CodecMs += decode + Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                        break;
                    }
                    case MessageType.Bye:
                        return;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (Exception e)
        {
            Log?.Invoke($"link failed: {e}");
        }
        finally
        {
            client.Dispose();
            if (world is not null)
            {
                world.Close();
                if (world.RecordingError is { } error)
                    Log?.Invoke($"world {world.Id}: recording stopped: {error}");
                else if (world.RecordingPath is { } path)
                    Log?.Invoke($"world {world.Id}: recorded to {path}");
                WorldClosed?.Invoke(world);
            }
        }
    }

    private World? Open(FrameStream frames, Manifest manifest)
    {
        Welcome welcome = new() { WorldId = Interlocked.Increment(ref _nextWorld) };
        foreach (string mode in manifest.ControlModes.Keys)
            (mode == "foot_shooter" ? welcome.Accepted : welcome.Rejected).Add("control_mode:" + mode);
        foreach (string objective in manifest.Objectives)
            (objective is "hold_zone" or "interact" or "score_race" ? welcome.Accepted : welcome.Rejected).Add("objective:" + objective);
        foreach (string capability in manifest.Capabilities)
            welcome.Rejected.Add("capability:" + capability);

        GridMap? map = maps.Find(manifest.Map.Id, manifest.Map.Hash);
        welcome.MapLoaded = map is not null;
        World? world = null;
        if (manifest.Protocol != Manifest.ProtocolVersion)
            Log?.Invoke($"{manifest.Title}: protocol {manifest.Protocol} is not supported");
        else if (map is null)
            Log?.Invoke($"{manifest.Title}: no map package for {manifest.Map.Id} ({manifest.Map.Hash})");
        else if (!manifest.ControlModes.ContainsKey("foot_shooter"))
            Log?.Invoke($"{manifest.Title}: no control mode this build can drive");
        else
            world = new World(welcome.WorldId, manifest, map, options);
        frames.WriteJson(MessageType.Welcome, welcome);
        if (world is not null)
            Log?.Invoke($"world {world.Id}: {manifest.Title} {manifest.Build} on {manifest.Map.Id}"
                + (world.RecordingPath is { } path ? $", recording to {path}" : world.RecordingError is { } error ? $", not recording: {error}" : ""));
        return world;
    }

    public void Dispose() => _listener?.Stop();
}
