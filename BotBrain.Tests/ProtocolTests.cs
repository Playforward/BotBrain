using System.Runtime.CompilerServices;
using BotBrain.Protocol;

namespace BotBrain.Tests;

public class ProtocolTests
{
    [Fact]
    public void Records_have_the_sizes_the_spec_implies()
    {
        Assert.Equal(50, Unsafe.SizeOf<EntityRecord>());
        Assert.Equal(23, Unsafe.SizeOf<AgentRecord>());
        Assert.Equal(32, Unsafe.SizeOf<ObserverRecord>());
        Assert.Equal(18, Unsafe.SizeOf<ActRecord>());
        Assert.Equal(19, Unsafe.SizeOf<CommandRecord>());
        Assert.Equal(29, Unsafe.SizeOf<EventRecord>());
    }

    [Fact]
    public void Observe_survives_encoding()
    {
        ObserveFrame sent = new() { Tick = 77, TimeMs = 2567, EntityCount = 2, AgentCount = 1, ObserverCount = 1 };
        sent.Entities[0] = new EntityRecord { Id = 5, Kind = EntityKind.Player, Team = 1, Flags = EntityFlags.Alive | EntityFlags.Human, X = 1.5f, Y = -2f, Yaw = 0.25f, Health = 200 };
        sent.Entities[1] = new EntityRecord { Id = 9, Kind = EntityKind.Vehicle, Team = 255, Vx = 12f, Parent = 5, Seat = 2 };
        sent.Agents[0] = new AgentRecord { Slot = 3, Entity = 5, ControlMode = ControlMode.Drive, AmmoInMag = 30, AmmoReserve = 90, Cooldowns = 0x0102030405060708, LockTarget = 9 };
        sent.Observers[0] = new ObserverRecord { Entity = 5, X = 1, Y = 2, Z = 3, Yaw = 0.5f, Fov = 1.4f, Spectating = 9 };

        byte[] buffer = new byte[16];
        int length = sent.Encode(ref buffer);
        ObserveFrame got = new();
        got.Decode(buffer.AsSpan(0, length));

        Assert.Equal(77u, got.Tick);
        Assert.Equal(2567u, got.TimeMs);
        Assert.Equal(2, got.EntityCount);
        Assert.Equal(sent.Entities[0], got.Entities[0]);
        Assert.Equal(sent.Entities[1], got.Entities[1]);
        Assert.Equal(sent.Agents[0], got.Agents[0]);
        Assert.Equal(sent.Observers[0], got.Observers[0]);
    }

    [Fact]
    public void Act_and_events_survive_encoding()
    {
        ActFrame act = new() { Tick = 12 };
        act.Add(new ActRecord { Slot = 4, FromTick = 12, Axis0 = -32767, Axis1 = 100, Axis2 = 32767, Axis3 = -5, Held = 0b101 });
        act.Commands[0] = new CommandRecord { Slot = 4, Id = 3, TargetEntity = 99, X = 1, Y = 2, Z = 3 };
        act.CommandCount = 1;
        byte[] buffer = new byte[4];
        ActFrame gotAct = new();
        int length = act.Encode(ref buffer);
        gotAct.Decode(buffer.AsSpan(0, length));
        Assert.Equal(12u, gotAct.Tick);
        Assert.Equal(act.Records[0], gotAct.Records[0]);
        Assert.Equal(act.Commands[0], gotAct.Commands[0]);

        EventBatch events = new() { Tick = 13 };
        events.Add(new EventRecord { Kind = EventKind.Damage, A = 1, B = 2, X = 0.6f, Y = 0.8f, Value = 25 });
        EventBatch gotEvents = new();
        length = events.Encode(ref buffer);
        gotEvents.Decode(buffer.AsSpan(0, length));
        Assert.Equal(1, gotEvents.Count);
        Assert.Equal(events.Events[0], gotEvents.Events[0]);
    }

    [Fact]
    public void A_truncated_frame_is_refused()
    {
        ObserveFrame frame = new() { EntityCount = 3 };
        byte[] buffer = new byte[16];
        int length = frame.Encode(ref buffer);
        Assert.Throws<InvalidDataException>(() => new ObserveFrame().Decode(buffer.AsSpan(0, length - 10)));
    }

    [Fact]
    public void Frames_and_json_cross_a_stream()
    {
        MemoryStream stream = new();
        FrameStream writer = new(stream);
        Manifest manifest = new() { Title = "t", Teams = new TeamsSpec { Ids = [0, 1, 2], Hostile = [[0, 1], [1, 2]] } };
        manifest.ControlModes["foot_shooter"] = new ControlModeSpec { Actions = ["fire", "reload"] };
        writer.WriteJson(MessageType.Hello, manifest);
        writer.Write(MessageType.Bye, []);

        stream.Position = 0;
        FrameStream reader = new(stream);
        Assert.True(reader.Read(out MessageType type, out ReadOnlyMemory<byte> payload));
        Assert.Equal(MessageType.Hello, type);
        Manifest got = Json.Decode<Manifest>(payload.Span);
        Assert.Equal("t", got.Title);
        Assert.Equal(["fire", "reload"], got.ControlModes["foot_shooter"].Actions);
        Assert.Equal(2, got.Teams.Hostile.Count);
        Assert.True(reader.Read(out type, out payload));
        Assert.Equal(MessageType.Bye, type);
        Assert.Equal(0, payload.Length);
        Assert.False(reader.Read(out _, out _));
    }
}
