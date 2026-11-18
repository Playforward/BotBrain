using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BotBrain.Protocol;

public enum ControlMode : byte
{
    FootShooter = 0,
    FootMeleeStealth = 1,
    Drive = 2,
    Turret = 3,
    Bike2D = 4,
    Fly = 5,
}

public enum EntityKind : byte
{
    Player = 0,
    Npc = 1,
    Vehicle = 2,
    Turret = 3,
    Projectile = 4,
    Deployable = 5,
    Pickup = 6,
    Structure = 7,
    ObjectiveMarker = 8,
}

[Flags]
public enum EntityFlags : ushort
{
    None = 0,
    Alive = 1,
    Human = 2,
    Downed = 4,
    Crouched = 8,
    Prone = 16,
    Aiming = 32,
    Firing = 64,
    Sprinting = 128,
    Airborne = 256,
    InSeat = 512,
}

public enum EventKind : byte
{
    /// <summary>A = victim, B = attacker (0 if unknown), XYZ = unit direction from victim to the source, Value = amount.</summary>
    Damage = 1,
    /// <summary>A = victim, B = killer, C = item.</summary>
    Death = 2,
    /// <summary>A = shooter, XYZ = where it was fired from.</summary>
    Shot = 3,
    /// <summary>XYZ = position, Value = radius.</summary>
    Explosion = 4,
    /// <summary>A = objective id, B = owning team (255 none), C = flags (<see cref="ObjectiveFlags"/>), Value = progress.</summary>
    Objective = 5,
    /// <summary>A = team, Value = score.</summary>
    Score = 6,
    Order = 7,
    Hint = 8,
    Revive = 9,
    SeatChange = 10,
    ControlMode = 11,
}

/// <summary>The C field of an objective event. Added 2026-11-17; a body that sends 0 means none of them.</summary>
[Flags]
public enum ObjectiveFlags : uint
{
    None = 0,
    /// <summary>Nobody can work it any more, whoever holds it.</summary>
    Locked = 1,
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct EntityRecord
{
    public uint Id;
    public EntityKind Kind;
    public byte Team;
    public EntityFlags Flags;
    public float X, Y, Z;
    public float Vx, Vy, Vz;
    public float Yaw, Pitch;
    public ushort Appearance;
    public ushort HeldItem;
    public byte Health;
    public uint Parent;
    public byte Seat;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct AgentRecord
{
    public ushort Slot;
    public uint Entity;
    public ControlMode ControlMode;
    public ushort AmmoInMag;
    public ushort AmmoReserve;
    /// <summary>Eight cooldowns, one byte each, in tenths of a second.</summary>
    public ulong Cooldowns;
    public uint LockTarget;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ObserverRecord
{
    public uint Entity;
    public float X, Y, Z;
    public float Yaw, Pitch, Fov;
    public uint Spectating;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ActRecord
{
    public ushort Slot;
    public uint FromTick;
    /// <summary>foot_shooter: move x (right), move y (forward), look x (right), look y (up). ±32767.</summary>
    public short Axis0, Axis1, Axis2, Axis3;
    public uint Held;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CommandRecord
{
    public ushort Slot;
    public byte Id;
    public uint TargetEntity;
    public float X, Y, Z;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct EventRecord
{
    public EventKind Kind;
    public uint A, B, C;
    public float X, Y, Z;
    public float Value;
}

internal static class Pack
{
    public static int Put<T>(ReadOnlySpan<T> source, Span<byte> destination) where T : unmanaged
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(source);
        bytes.CopyTo(destination);
        return bytes.Length;
    }

    public static int Get<T>(ReadOnlySpan<byte> source, int count, ref T[] destination) where T : unmanaged
    {
        int size = count * Unsafe.SizeOf<T>();
        if (source.Length < size)
            throw new InvalidDataException($"frame too short for {count} {typeof(T).Name}");
        if (destination.Length < count)
            destination = new T[Math.Max(count, destination.Length * 2)];
        source[..size].CopyTo(MemoryMarshal.AsBytes(destination.AsSpan(0, count)));
        return size;
    }

    public static void Reserve(ref byte[] buffer, int size)
    {
        if (buffer.Length < size)
            buffer = new byte[Math.Max(size, buffer.Length * 2)];
    }
}

/// <summary>One tick of ground truth from a body (spec section 7.1). Reused between ticks.</summary>
public sealed class ObserveFrame
{
    public uint Tick;
    public uint TimeMs;
    public EntityRecord[] Entities = new EntityRecord[256];
    public int EntityCount;
    public AgentRecord[] Agents = new AgentRecord[256];
    public int AgentCount;
    public ObserverRecord[] Observers = new ObserverRecord[8];
    public int ObserverCount;

    public int Encode(ref byte[] buffer)
    {
        int size = 13
            + EntityCount * Unsafe.SizeOf<EntityRecord>()
            + AgentCount * Unsafe.SizeOf<AgentRecord>()
            + ObserverCount * Unsafe.SizeOf<ObserverRecord>();
        Pack.Reserve(ref buffer, size);
        Span<byte> s = buffer;
        BinaryPrimitives.WriteUInt32LittleEndian(s, Tick);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], TimeMs);
        BinaryPrimitives.WriteUInt16LittleEndian(s[8..], (ushort)EntityCount);
        BinaryPrimitives.WriteUInt16LittleEndian(s[10..], (ushort)AgentCount);
        s[12] = (byte)ObserverCount;
        int o = 13;
        o += Pack.Put<EntityRecord>(Entities.AsSpan(0, EntityCount), s[o..]);
        o += Pack.Put<AgentRecord>(Agents.AsSpan(0, AgentCount), s[o..]);
        o += Pack.Put<ObserverRecord>(Observers.AsSpan(0, ObserverCount), s[o..]);
        return o;
    }

    public void Decode(ReadOnlySpan<byte> p)
    {
        if (p.Length < 13)
            throw new InvalidDataException("OBSERVE too short");
        Tick = BinaryPrimitives.ReadUInt32LittleEndian(p);
        TimeMs = BinaryPrimitives.ReadUInt32LittleEndian(p[4..]);
        EntityCount = BinaryPrimitives.ReadUInt16LittleEndian(p[8..]);
        AgentCount = BinaryPrimitives.ReadUInt16LittleEndian(p[10..]);
        ObserverCount = p[12];
        int o = 13;
        o += Pack.Get(p[o..], EntityCount, ref Entities);
        o += Pack.Get(p[o..], AgentCount, ref Agents);
        Pack.Get(p[o..], ObserverCount, ref Observers);
    }
}

/// <summary>Controls for the agents on a link (spec section 7.2). Reused between ticks.</summary>
public sealed class ActFrame
{
    /// <summary>The OBSERVE tick this answers.</summary>
    public uint Tick;
    public ActRecord[] Records = new ActRecord[256];
    public int Count;
    public CommandRecord[] Commands = new CommandRecord[16];
    public int CommandCount;

    public void Add(in ActRecord record)
    {
        if (Count == Records.Length)
            Array.Resize(ref Records, Count * 2);
        Records[Count++] = record;
    }

    public int Encode(ref byte[] buffer)
    {
        int size = 8 + Count * Unsafe.SizeOf<ActRecord>() + CommandCount * Unsafe.SizeOf<CommandRecord>();
        Pack.Reserve(ref buffer, size);
        Span<byte> s = buffer;
        BinaryPrimitives.WriteUInt32LittleEndian(s, Tick);
        BinaryPrimitives.WriteUInt16LittleEndian(s[4..], (ushort)Count);
        BinaryPrimitives.WriteUInt16LittleEndian(s[6..], (ushort)CommandCount);
        int o = 8;
        o += Pack.Put<ActRecord>(Records.AsSpan(0, Count), s[o..]);
        o += Pack.Put<CommandRecord>(Commands.AsSpan(0, CommandCount), s[o..]);
        return o;
    }

    public void Decode(ReadOnlySpan<byte> p)
    {
        if (p.Length < 8)
            throw new InvalidDataException("ACT too short");
        Tick = BinaryPrimitives.ReadUInt32LittleEndian(p);
        Count = BinaryPrimitives.ReadUInt16LittleEndian(p[4..]);
        CommandCount = BinaryPrimitives.ReadUInt16LittleEndian(p[6..]);
        int o = 8;
        o += Pack.Get(p[o..], Count, ref Records);
        Pack.Get(p[o..], CommandCount, ref Commands);
    }
}

/// <summary>Events that happened since the last batch (spec section 6.7). Reused between ticks.</summary>
public sealed class EventBatch
{
    public uint Tick;
    public EventRecord[] Events = new EventRecord[256];
    public int Count;

    public void Add(in EventRecord e)
    {
        if (Count == Events.Length)
            Array.Resize(ref Events, Count * 2);
        Events[Count++] = e;
    }

    public int Encode(ref byte[] buffer)
    {
        int size = 6 + Count * Unsafe.SizeOf<EventRecord>();
        Pack.Reserve(ref buffer, size);
        Span<byte> s = buffer;
        BinaryPrimitives.WriteUInt32LittleEndian(s, Tick);
        BinaryPrimitives.WriteUInt16LittleEndian(s[4..], (ushort)Count);
        return 6 + Pack.Put<EventRecord>(Events.AsSpan(0, Count), s[6..]);
    }

    public void Decode(ReadOnlySpan<byte> p)
    {
        if (p.Length < 6)
            throw new InvalidDataException("EVENT too short");
        Tick = BinaryPrimitives.ReadUInt32LittleEndian(p);
        Count = BinaryPrimitives.ReadUInt16LittleEndian(p[4..]);
        Pack.Get(p[6..], Count, ref Events);
    }
}
