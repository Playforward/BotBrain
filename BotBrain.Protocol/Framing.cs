using System.Buffers.Binary;

namespace BotBrain.Protocol;

/// <summary>Message types of the attach protocol (docs/ATTACH-PROTOCOL.md, section 4).</summary>
public enum MessageType : byte
{
    Hello = 1,
    Welcome = 2,
    Seats = 3,
    SeatTake = 4,
    SeatLeave = 5,
    Phase = 6,
    Choice = 7,
    ChoiceReply = 8,
    Observe = 9,
    Act = 10,
    Event = 11,
    Rate = 12,
    Bye = 13,
    Objectives = 14,
}

/// <summary>
/// Frames on a stream: u32 length (type + payload), u8 type, payload.
/// Little-endian. One reader and any number of writers.
/// </summary>
public sealed class FrameStream(Stream stream) : IDisposable
{
    public const int MaxFrame = 16 * 1024 * 1024;

    private readonly Lock _writeLock = new();
    private readonly byte[] _header = new byte[5];
    private byte[] _readBuffer = new byte[64 * 1024];
    private byte[] _writeBuffer = new byte[64 * 1024];

    public void Write(MessageType type, ReadOnlySpan<byte> payload)
    {
        lock (_writeLock)
        {
            int total = payload.Length + 5;
            if (_writeBuffer.Length < total)
                _writeBuffer = new byte[Math.Max(total, _writeBuffer.Length * 2)];
            BinaryPrimitives.WriteUInt32LittleEndian(_writeBuffer, (uint)(payload.Length + 1));
            _writeBuffer[4] = (byte)type;
            payload.CopyTo(_writeBuffer.AsSpan(5));
            stream.Write(_writeBuffer, 0, total);
            stream.Flush();
        }
    }

    public void WriteJson<T>(MessageType type, T value) => Write(type, Json.Encode(value));

    /// <summary>
    /// Reads the next frame. Returns false on a clean end of stream. The
    /// payload is only valid until the next call.
    /// </summary>
    public bool Read(out MessageType type, out ReadOnlyMemory<byte> payload)
    {
        type = 0;
        payload = default;
        if (!Fill(_header, 5, allowEnd: true))
            return false;
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(_header);
        if (length < 1 || length > MaxFrame)
            throw new InvalidDataException($"frame length {length} out of range");
        type = (MessageType)_header[4];
        int size = (int)length - 1;
        if (_readBuffer.Length < size)
            _readBuffer = new byte[Math.Max(size, _readBuffer.Length * 2)];
        Fill(_readBuffer, size, allowEnd: false);
        payload = _readBuffer.AsMemory(0, size);
        return true;
    }

    private bool Fill(byte[] buffer, int count, bool allowEnd)
    {
        int got = 0;
        while (got < count)
        {
            int n = stream.Read(buffer, got, count - got);
            if (n == 0)
            {
                if (got == 0 && allowEnd)
                    return false;
                throw new EndOfStreamException("stream ended inside a frame");
            }
            got += n;
        }
        return true;
    }

    public void Dispose() => stream.Dispose();
}
