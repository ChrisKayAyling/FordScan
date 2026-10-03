namespace FordDiag.Comms;

/// <summary>ISO 15765-2 frame types.</summary>
public enum IsoTpFrameType { Single = 0, First = 1, Consecutive = 2, FlowControl = 3 }

/// <summary>Parsed flow-control frame.</summary>
public readonly record struct FlowControl(byte Status, int BlockSize, TimeSpan SeparationTime)
{
    public bool ClearToSend => Status == 0;
    public bool Wait => Status == 1;
    public bool Overflow => Status == 2;

    /// <summary>Decodes STmin per ISO 15765-2 (0-0x7F ms, 0xF1-0xF9 100-900 µs).</summary>
    public static TimeSpan DecodeStMin(byte b) => b switch
    {
        <= 0x7F => TimeSpan.FromMilliseconds(b),
        >= 0xF1 and <= 0xF9 => TimeSpan.FromTicks((b - 0xF0) * 100 * TimeSpan.TicksPerMicrosecond),
        _ => TimeSpan.FromMilliseconds(127),
    };

    public static byte EncodeStMin(TimeSpan t) => t.TotalMilliseconds >= 1
        ? (byte)Math.Min(127, (int)t.TotalMilliseconds)
        : t > TimeSpan.Zero ? (byte)(0xF0 + Math.Clamp((int)(t.TotalMicroseconds / 100), 1, 9)) : (byte)0;
}

/// <summary>Stateless ISO-TP helpers (frame building / parsing) shared by the ELM driver and the simulated ECUs.</summary>
public static class IsoTp
{
    /// <summary>
    /// Splits a payload into CAN frames: one single frame, or a first frame + consecutive frames.
    /// <paramref name="extAddress"/> (CAN extended addressing) is prepended to every frame and reduces capacity by one byte.
    /// Frames are not padded unless <paramref name="padByte"/> is given.
    /// </summary>
    public static List<byte[]> Segment(ReadOnlySpan<byte> payload, byte? extAddress = null, byte? padByte = null)
    {
        int ext = extAddress.HasValue ? 1 : 0;
        int sfMax = 7 - ext;
        var frames = new List<byte[]>();
        if (payload.Length <= sfMax)
        {
            var f = new byte[ext + 1 + payload.Length];
            if (ext == 1) f[0] = extAddress!.Value;
            f[ext] = (byte)payload.Length;
            payload.CopyTo(f.AsSpan(ext + 1));
            frames.Add(Pad(f, padByte));
            return frames;
        }
        if (payload.Length > 4095) throw new ProtocolException("ISO-TP payload longer than 4095 bytes is not supported.");

        int ffData = 6 - ext;
        var ff = new byte[8];
        if (ext == 1) ff[0] = extAddress!.Value;
        ff[ext] = (byte)(0x10 | (payload.Length >> 8));
        ff[ext + 1] = (byte)payload.Length;
        payload[..ffData].CopyTo(ff.AsSpan(ext + 2));
        frames.Add(ff);

        int cfData = 7 - ext;
        int pos = ffData, sn = 1;
        while (pos < payload.Length)
        {
            int n = Math.Min(cfData, payload.Length - pos);
            var cf = new byte[ext + 1 + n];
            if (ext == 1) cf[0] = extAddress!.Value;
            cf[ext] = (byte)(0x20 | (sn & 0xF));
            payload.Slice(pos, n).CopyTo(cf.AsSpan(ext + 1));
            frames.Add(Pad(cf, padByte));
            pos += n; sn++;
        }
        return frames;
    }

    private static byte[] Pad(byte[] frame, byte? pad)
    {
        if (pad is null || frame.Length >= 8) return frame;
        var p = new byte[8];
        frame.CopyTo(p, 0);
        Array.Fill(p, pad.Value, frame.Length, 8 - frame.Length);
        return p;
    }

    /// <summary>Builds a flow-control frame.</summary>
    public static byte[] BuildFlowControl(byte status, int blockSize, byte stMin, byte? extAddress = null)
    {
        return extAddress.HasValue
            ? new[] { extAddress.Value, (byte)(0x30 | status), (byte)blockSize, stMin }
            : new[] { (byte)(0x30 | status), (byte)blockSize, stMin };
    }

    /// <summary>Frame type from the PCI nibble (after skipping <paramref name="extOffset"/> address bytes).</summary>
    public static IsoTpFrameType TypeOf(ReadOnlySpan<byte> frame, int extOffset = 0)
    {
        if (frame.Length <= extOffset) throw new ProtocolException("Empty CAN frame.");
        int t = frame[extOffset] >> 4;
        return t is >= 0 and <= 3 ? (IsoTpFrameType)t : throw new ProtocolException($"Invalid ISO-TP PCI 0x{frame[extOffset]:X2}.");
    }

    public static FlowControl ParseFlowControl(ReadOnlySpan<byte> frame, int extOffset = 0)
    {
        if (TypeOf(frame, extOffset) != IsoTpFrameType.FlowControl) throw new ProtocolException("Not a flow-control frame.");
        byte status = (byte)(frame[extOffset] & 0xF);
        int bs = frame.Length > extOffset + 1 ? frame[extOffset + 1] : 0;
        byte st = frame.Length > extOffset + 2 ? frame[extOffset + 2] : (byte)0;
        return new FlowControl(status, bs, FlowControl.DecodeStMin(st));
    }

    /// <summary>True if <paramref name="payload"/> is a "7F sid 78" response-pending message (optionally for a given sid).</summary>
    public static bool IsPending(ReadOnlySpan<byte> payload, int? sid = null) =>
        payload.Length >= 3 && payload[0] == 0x7F && payload[2] == 0x78 && (sid is null || payload[1] == sid);
}

/// <summary>Incremental ISO-TP receiver (single frames and segmented messages).</summary>
public sealed class IsoTpReassembler
{
    private readonly int _ext;
    private byte[]? _buffer;
    private int _filled;
    private int _nextSn;

    /// <param name="extOffset">Number of leading extended-address bytes to skip in every frame (0 or 1).</param>
    public IsoTpReassembler(int extOffset = 0) => _ext = extOffset;

    public bool IsComplete { get; private set; }
    public bool InProgress => _buffer is not null && !IsComplete;
    public int ExpectedLength => _buffer?.Length ?? 0;
    public int Received => _filled;

    /// <summary>The reassembled payload once <see cref="IsComplete"/>.</summary>
    public byte[] Payload => IsComplete ? _buffer! : throw new InvalidOperationException("Message not complete.");

    /// <summary>Consecutive frames still missing.</summary>
    public int FramesRemaining
    {
        get
        {
            if (_buffer is null || IsComplete) return 0;
            int per = 7 - _ext;
            return (_buffer.Length - _filled + per - 1) / per;
        }
    }

    public void Reset() { _buffer = null; _filled = 0; _nextSn = 0; IsComplete = false; }

    /// <summary>Feeds one frame. Flow-control frames are rejected (they belong to the sending side).</summary>
    public IsoTpFrameType Feed(ReadOnlySpan<byte> frame)
    {
        var type = IsoTp.TypeOf(frame, _ext);
        switch (type)
        {
            case IsoTpFrameType.Single:
            {
                int len = frame[_ext] & 0xF;
                if (len == 0 || frame.Length < _ext + 1 + len)
                    throw new ProtocolException("Malformed ISO-TP single frame.");
                _buffer = frame.Slice(_ext + 1, len).ToArray();
                _filled = len; IsComplete = true;
                break;
            }
            case IsoTpFrameType.First:
            {
                if (frame.Length < _ext + 2) throw new ProtocolException("Malformed ISO-TP first frame.");
                int len = ((frame[_ext] & 0xF) << 8) | frame[_ext + 1];
                if (len < 8 - _ext) throw new ProtocolException("ISO-TP first frame length too small.");
                _buffer = new byte[len];
                int n = Math.Min(len, frame.Length - _ext - 2);
                frame.Slice(_ext + 2, n).CopyTo(_buffer);
                _filled = n; _nextSn = 1; IsComplete = false;
                break;
            }
            case IsoTpFrameType.Consecutive:
            {
                if (_buffer is null || IsComplete) throw new ProtocolException("Unexpected ISO-TP consecutive frame.");
                int sn = frame[_ext] & 0xF;
                if (sn != (_nextSn & 0xF)) throw new ProtocolException($"ISO-TP sequence error: expected {_nextSn & 0xF}, got {sn}.");
                _nextSn++;
                int n = Math.Min(_buffer.Length - _filled, frame.Length - _ext - 1);
                frame.Slice(_ext + 1, n).CopyTo(_buffer.AsSpan(_filled));
                _filled += n;
                if (_filled >= _buffer.Length) IsComplete = true;
                break;
            }
            default:
                throw new ProtocolException("Flow-control frame passed to the receiver.");
        }
        return type;
    }
}
