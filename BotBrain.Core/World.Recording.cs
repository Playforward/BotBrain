using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BotBrain.Maps;
using BotBrain.Protocol;

namespace BotBrain.Core;

/// <summary>
/// Writes a match to a file as the brain lived it, for the replay viewer
/// (<c>viewer/index.html</c>): everything the body reported, and for each
/// agent what it believed, what it was trying to do and what its hands did.
/// Nothing here depends on the game: any body's match records the same way.
/// <para>The file is one gzip stream (all numbers little-endian):</para>
/// <code>
/// "BBRC", u32 version (1)
/// u32 length, header as JSON
/// width * height bytes: the map from above, 0 = not walkable, 1..255 = ground height
/// blocks until the end, each: u8 kind, u32 length, payload
///   1 agent       JSON: slot, team, name, persona
///   2 objectives  JSON: the OBJECTIVES message as received
///   3 frame       u32 tick, u32 time ms,
///                 u16 n, n entities of 29 bytes:
///                   u32 id, u8 kind, u8 team, u16 flags, f32 x y z, f32 yaw, f32 pitch, u8 health
///                 u16 n, n agents:
///                   u16 slot, u32 entity, u8 goal, u8 tier, u32 target entity,
///                   i16 move x, move y, look x, look y, u32 held, u16 ammo,
///                   f32 steer x y, f32 destination x y, f32 look point x y z  (NaN = none),
///                   u8 n, n tracks of 17 bytes: u32 entity, u8 state, f32 x y, f32 seconds since seen
///   4 events      u32 tick, u16 n, n event records as on the wire (29 bytes)
///   5 contacts    JSON: tick, contacts [{team, x, y, age}]: where each team believes
///                 the enemy has lately been; written when it changes, at most once a second
///   6 hunt        JSON: tick, slot, reason (Revenge, Gunfire), x, y, via [x, y] or null:
///                 an agent set off after an enemy believed to be at x, y, round the side by via
///   7 camp        JSON: tick, slot, x, y, watch (radians): an agent set off to camp at x, y, facing watch
///   8 role        JSON: tick, slot, role (Seeker, Defender, Attacker), site (objective id or -1), x, y:
///                 an agent's role, and the objective it now plays (written when either changes)
///   9 floors      width * height bytes, once after the header, on a map with a navigation mesh:
///                 the lowest floor under each cell where it lies 2 m or more below the top floor
///                 the map from above shows (same height scale; 0 = no second floor)
/// Readers skip block kinds they do not know, so these leave version 1 readable.
/// </code>
/// </summary>
public sealed partial class World
{
    private const uint RecordingMagic = 0x43524242; // "BBRC"
    private const byte BlockAgent = 1, BlockObjectives = 2, BlockFrame = 3, BlockEvents = 4, BlockContacts = 5, BlockHunt = 6, BlockCamp = 7, BlockRole = 8, BlockFloors = 9;

    private Stream? _recording;
    private FileStream? _recordingFile;
    private byte[] _recordBuffer = new byte[64 * 1024];
    private int _recordEvery = 1;
    private uint _recordedFrames;
    private double _recordFlushAt;

    /// <summary>The file this match is being recorded to, or null.</summary>
    public string? RecordingPath { get; private set; }

    /// <summary>Why recording stopped or never started, or null.</summary>
    public string? RecordingError { get; private set; }

    private void StartRecording(Manifest manifest)
    {
        if (string.IsNullOrEmpty(_options.RecordDirectory))
            return;
        try
        {
            string folder = Path.GetFullPath(_options.RecordDirectory);
            Directory.CreateDirectory(folder);
            string name = $"{Safe(manifest.Title)}-{Safe(_map.Id)}-{DateTime.Now:yyyyMMdd-HHmmss}-w{Id}.bbrec";
            RecordingPath = Path.Combine(folder, name);
            _recordingFile = new FileStream(RecordingPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            _recording = new GZipStream(_recordingFile, CompressionLevel.Fastest);

            (byte[] raster, byte[]? lower, float low, float high) = MapFromAbove();
            manifest.ControlModes.TryGetValue("foot_shooter", out ControlModeSpec? mode);
            byte[] header = JsonSerializer.SerializeToUtf8Bytes(new
            {
                title = manifest.Title,
                build = manifest.Build,
                body_kind = manifest.BodyKind,
                tick_hz = _tickHz,
                world_id = Id,
                started_utc = DateTime.UtcNow.ToString("o"),
                seed = _options.Seed,
                actions = mode?.Actions ?? [],
                look = mode?.Look,
                move = mode?.Move,
                goals = Enum.GetNames<Goal>(),
                track_states = Enum.GetNames<TrackState>(),
                tiers = Enum.GetNames<Tier>(),
                map = new
                {
                    id = _map.Id,
                    hash = _map.Hash(),
                    width = _map.Width,
                    height = _map.Height,
                    cell = _map.CellSize,
                    origin_x = _map.Origin.X,
                    origin_y = _map.Origin.Y,
                    ground_low = low,
                    ground_high = high,
                    points = _map.Points.Select(p => new { kind = p.Kind.ToString(), team = p.Team, id = p.Id, x = p.Position.X, y = p.Position.Y, z = _map.PointAt(p).Z, radius = p.Radius }),
                    floors = lower is not null,
                },
            }, Json.Options);
            Span<byte> start = stackalloc byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(start, RecordingMagic);
            BinaryPrimitives.WriteUInt32LittleEndian(start[4..], 1);
            BinaryPrimitives.WriteInt32LittleEndian(start[8..], header.Length);
            _recording.Write(start);
            _recording.Write(header);
            _recording.Write(raster);
            if (lower is not null)
            {
                Span<byte> head = stackalloc byte[5];
                head[0] = BlockFloors;
                BinaryPrimitives.WriteInt32LittleEndian(head[1..], lower.Length);
                _recording.Write(head);
                _recording.Write(lower);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            StopRecording(e.Message);
        }
    }

    private static string Safe(string text)
    {
        StringBuilder safe = new();
        foreach (char c in text)
            safe.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
        return safe.Length == 0 ? "match" : safe.ToString();
    }

    /// <summary>One byte a cell: 0 where nobody can walk, otherwise the ground height spread over 1..255.</summary>
    private (byte[] Raster, byte[]? Lower, float Low, float High) MapFromAbove()
    {
        int cells = _map.Width * _map.Height;
        if (_map.Nav is { } nav)
        {
            // Every floor of the mesh: the top one from above, and where a lower one lies under it.
            (float[] top, float[] bottom, _) = nav.FloorsFromAbove(_map.Origin, _map.CellSize, _map.Width, _map.Height);
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (float z in top.Concat(bottom))
            {
                if (float.IsNaN(z))
                    continue;
                lo = MathF.Min(lo, z);
                hi = MathF.Max(hi, z);
            }
            if (lo > hi)
                lo = hi = 0;
            float k = hi - lo > 0.01f ? 254f / (hi - lo) : 0;
            byte[] upper = new byte[cells], under = new byte[cells];
            for (int i = 0; i < cells; i++)
            {
                if (float.IsNaN(top[i]))
                    continue;
                upper[i] = (byte)(1 + Math.Clamp((int)((top[i] - lo) * k), 0, 254));
                if (top[i] - bottom[i] >= 2f)
                    under[i] = (byte)(1 + Math.Clamp((int)((bottom[i] - lo) * k), 0, 254));
            }
            return (upper, under, lo, hi);
        }
        byte[] raster = new byte[cells];
        float low = float.MaxValue, high = float.MinValue;
        float[] ground = new float[cells];
        for (int cy = 0; cy < _map.Height; cy++)
        {
            for (int cx = 0; cx < _map.Width; cx++)
            {
                if (_map.IsBlockedCell(cx, cy))
                    continue;
                float g = _map.GroundAt(_map.CellCenter(cx, cy));
                ground[cy * _map.Width + cx] = g;
                low = MathF.Min(low, g);
                high = MathF.Max(high, g);
            }
        }
        if (low > high)
            low = high = 0;
        float scale = high - low > 0.01f ? 254f / (high - low) : 0;
        for (int cy = 0; cy < _map.Height; cy++)
        {
            for (int cx = 0; cx < _map.Width; cx++)
            {
                int i = cy * _map.Width + cx;
                if (!_map.IsBlockedCell(cx, cy))
                    raster[i] = (byte)(1 + Math.Clamp((int)((ground[i] - low) * scale), 0, 254));
            }
        }
        return (raster, null, low, high);
    }

    private void RecordJson(byte kind, object value)
    {
        if (_recording is null)
            return;
        try
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, Json.Options);
            Span<byte> head = stackalloc byte[5];
            head[0] = kind;
            BinaryPrimitives.WriteInt32LittleEndian(head[1..], payload.Length);
            _recording.Write(head);
            _recording.Write(payload);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            StopRecording(e.Message);
        }
    }

    private void RecordContacts()
    {
        if (_recording is null)
            return;
        RecordJson(BlockContacts, new
        {
            tick = _tick,
            contacts = _contacts.Select(c => new { team = (int)c.Team, x = c.Position.X, y = c.Position.Y, age = MathF.Round((float)(_now - c.At), 1) }),
        });
    }

    private void RecordHunt(Agent a)
    {
        if (_recording is null)
            return;
        RecordJson(BlockHunt, new
        {
            tick = _tick,
            slot = a.Slot,
            reason = a.Hunt.ToString(),
            x = a.HuntPoint.X,
            y = a.HuntPoint.Y,
            via = a.HasHuntVia ? new[] { a.HuntVia.X, a.HuntVia.Y } : null,
        });
    }

    private void RecordCamp(Agent a)
    {
        if (_recording is null || a.CampSpot < 0)
            return;
        Vector2 at = _campPoints![a.CampSpot].Position;
        RecordJson(BlockCamp, new { tick = _tick, slot = a.Slot, x = at.X, y = at.Y, watch = a.CampWatch });
    }

    private void RecordRole(Agent a)
    {
        if (_recording is null)
            return;
        bool has = (uint)a.Site < (uint)_sites.Length;
        Vector2 at = has ? _sites[a.Site].Stand : default;
        RecordJson(BlockRole, new { tick = _tick, slot = a.Slot, role = a.Role.ToString(), site = has ? _sites[a.Site].Id : -1, x = at.X, y = at.Y });
    }

    private void RecordAgent(Agent agent) =>
        RecordJson(BlockAgent, new { slot = agent.Slot, team = (int)agent.Team, name = agent.P.Name, squad = agent.Squad, persona = agent.P });

    private void RecordEvents(EventBatch batch)
    {
        if (_recording is null || batch.Count == 0)
            return;
        int size = 5 + 6 + batch.Count * Marshal.SizeOf<EventRecord>();
        Pack(ref _recordBuffer, size);
        Span<byte> s = _recordBuffer;
        s[0] = BlockEvents;
        BinaryPrimitives.WriteInt32LittleEndian(s[1..], size - 5);
        // Events carry the tick the brain last saw, so they line up with its frames.
        BinaryPrimitives.WriteUInt32LittleEndian(s[5..], _tick);
        BinaryPrimitives.WriteUInt16LittleEndian(s[9..], (ushort)batch.Count);
        MemoryMarshal.AsBytes(batch.Events.AsSpan(0, batch.Count)).CopyTo(s[11..]);
        WriteRecorded(size);
    }

    private void RecordFrame(ObserveFrame frame)
    {
        if (_recording is null)
            return;
        if (_recordedFrames == 0)
            _recordEvery = _options.RecordEveryTicks > 0 ? _options.RecordEveryTicks : frame.EntityCount > 64 ? 3 : 1;
        if (_recordedFrames++ % (uint)_recordEvery != 0)
            return;

        int size = 5 + 8 + 2 + frame.EntityCount * 29 + 2 + _agents.Count * (48 + MaxTracks * 17);
        Pack(ref _recordBuffer, size);
        Span<byte> s = _recordBuffer;
        s[0] = BlockFrame;
        int o = 5;
        BinaryPrimitives.WriteUInt32LittleEndian(s[o..], frame.Tick);
        BinaryPrimitives.WriteUInt32LittleEndian(s[(o + 4)..], frame.TimeMs);
        BinaryPrimitives.WriteUInt16LittleEndian(s[(o + 8)..], (ushort)frame.EntityCount);
        o += 10;
        for (int i = 0; i < frame.EntityCount; i++)
        {
            ref EntityRecord e = ref frame.Entities[i];
            BinaryPrimitives.WriteUInt32LittleEndian(s[o..], e.Id);
            s[o + 4] = (byte)e.Kind;
            s[o + 5] = e.Team;
            BinaryPrimitives.WriteUInt16LittleEndian(s[(o + 6)..], (ushort)e.Flags);
            Put(s, o + 8, e.X);
            Put(s, o + 12, e.Y);
            Put(s, o + 16, e.Z);
            Put(s, o + 20, e.Yaw);
            Put(s, o + 24, e.Pitch);
            s[o + 28] = e.Health;
            o += 29;
        }

        int countAt = o, count = 0;
        o += 2;
        foreach (Agent a in _agents)
        {
            if (a.EntityIndex < 0)
                continue;
            count++;
            BinaryPrimitives.WriteUInt16LittleEndian(s[o..], (ushort)a.Slot);
            BinaryPrimitives.WriteUInt32LittleEndian(s[(o + 2)..], a.EntityId);
            s[o + 6] = (byte)(a.Alive ? a.Goal : Goal.None);
            s[o + 7] = (byte)a.Tier;
            BinaryPrimitives.WriteUInt32LittleEndian(s[(o + 8)..], a.Alive && a.Goal is Goal.Engage or Goal.Investigate ? a.TargetEntity : 0);
            BinaryPrimitives.WriteInt16LittleEndian(s[(o + 12)..], a.Sent.Axis0);
            BinaryPrimitives.WriteInt16LittleEndian(s[(o + 14)..], a.Sent.Axis1);
            BinaryPrimitives.WriteInt16LittleEndian(s[(o + 16)..], a.Sent.Axis2);
            BinaryPrimitives.WriteInt16LittleEndian(s[(o + 18)..], a.Sent.Axis3);
            BinaryPrimitives.WriteUInt32LittleEndian(s[(o + 20)..], a.Sent.Held);
            BinaryPrimitives.WriteUInt16LittleEndian(s[(o + 24)..], (ushort)Math.Clamp(a.Ammo, 0, ushort.MaxValue));
            Vector2 none = new(float.NaN);
            Vector2 steer = a.Alive && a.HasSteer ? a.Steer : none;
            Vector2 destination = a.Alive ? DestinationOf(a) ?? none : none;
            Put(s, o + 26, steer.X);
            Put(s, o + 30, steer.Y);
            Put(s, o + 34, destination.X);
            Put(s, o + 38, destination.Y);
            bool looking = a.Alive && a.Intent.HasLook;
            Put(s, o + 42, looking ? a.Intent.LookPoint.X : float.NaN);
            Put(s, o + 46, looking ? a.Intent.LookPoint.Y : float.NaN);
            Put(s, o + 50, looking ? a.Intent.LookPoint.Z : float.NaN);
            int tracksAt = o + 54, tracks = 0;
            o += 55;
            for (int t = 0; a.Alive && t < a.Tracks.Length; t++)
            {
                ref Track track = ref a.Tracks[t];
                if (track.State == TrackState.Free)
                    continue;
                tracks++;
                BinaryPrimitives.WriteUInt32LittleEndian(s[o..], track.Entity);
                s[o + 4] = (byte)track.State;
                Put(s, o + 5, track.Position.X);
                Put(s, o + 9, track.Position.Y);
                Put(s, o + 13, (float)(_now - track.SeenAt));
                o += 17;
            }
            s[tracksAt] = (byte)tracks;
        }
        BinaryPrimitives.WriteUInt16LittleEndian(s[countAt..], (ushort)count);
        BinaryPrimitives.WriteInt32LittleEndian(s[1..], o - 5);
        WriteRecorded(o);

        if (_now >= _recordFlushAt)
        {
            _recordFlushAt = _now + 5;
            try
            {
                _recording?.Flush();
                _recordingFile?.Flush();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                StopRecording(e.Message);
            }
        }
    }

    /// <summary>Where an agent is headed, if its goal has a place.</summary>
    private Vector2? DestinationOf(Agent a)
    {
        switch (a.Goal)
        {
            case Goal.Engage or Goal.Investigate when a.TargetTrack >= 0 && a.Tracks[a.TargetTrack].State != TrackState.Free:
                return a.Tracks[a.TargetTrack].Position;
            case Goal.GoZone when (uint)a.Zone < (uint)_zones.Length:
                return _zones[a.Zone].Position;
            case Goal.Hold when a.HasHoldPoint:
                return a.HoldPoint;
            case Goal.Roam when (uint)a.RoamTarget < (uint)_roamPoints.Length:
                return _roamPoints[a.RoamTarget].Position;
            case Goal.Hunt when a.Hunt != HuntReason.None:
                return a.HuntPoint;
            case Goal.Camp when a.CampSpot >= 0:
                return _campPoints![a.CampSpot].Position;
            case Goal.Objective when (uint)a.Site < (uint)_sites.Length:
                return a.HasPost && !a.Interacting ? a.PostPoint : _sites[a.Site].Stand;
            default:
                return null;
        }
    }

    private static void Put(Span<byte> s, int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(s[offset..], value);

    private static void Pack(ref byte[] buffer, int size)
    {
        if (buffer.Length < size)
            buffer = new byte[Math.Max(size, buffer.Length * 2)];
    }

    private void WriteRecorded(int length)
    {
        try
        {
            _recording?.Write(_recordBuffer, 0, length);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            StopRecording(e.Message);
        }
    }

    private void StopRecording(string? error)
    {
        RecordingError ??= error;
        Stream? stream = _recording;
        FileStream? file = _recordingFile;
        _recording = null;
        _recordingFile = null;
        try
        {
            stream?.Dispose();
            file?.Dispose();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            RecordingError ??= e.Message;
        }
    }

    /// <summary>Ends the match: finishes its recording, if it has one.</summary>
    public void Close() => StopRecording(null);
}
