using System.Globalization;

namespace FordDiag.Core.Coding;

public enum FieldKind { Enum, Value, Ascii, Hex }

/// <summary>
/// One named option inside a configuration block. <see cref="Byte"/> is relative to the block start; <see cref="Bit"/> is the
/// offset of the field's least significant bit inside its last byte (0 = bit 0x01). A field may span several bytes (big-endian).
/// </summary>
public sealed class CodingField
{
    public required string Name { get; init; }
    public int Byte { get; init; }
    public int Bit { get; init; }
    public int Size { get; init; }
    public FieldKind Kind { get; init; }
    public IReadOnlyDictionary<long, string> Options { get; init; } = new Dictionary<long, string>();
    public double Multiplier { get; init; } = 1;
    public double Offset { get; init; }
    public string? Unit { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    /// <summary>Free-text note from the definition source.</summary>
    public string? Note { get; init; }
    /// <summary>As-built line the option is documented at, e.g. "726-01-01".</summary>
    public string? Loc { get; init; }

    public int ByteSpan => (Bit + Size + 7) / 8;
    public long MaxRaw => Size >= 63 ? long.MaxValue : (1L << Size) - 1;
    public bool Fits(int blockLength) => Size is > 0 and <= 32 && Bit >= 0 && Bit < 8 && Byte >= 0 && Byte + ByteSpan <= blockLength;

    public long ReadRaw(ReadOnlySpan<byte> block)
    {
        long window = 0;
        for (int i = 0; i < ByteSpan; i++) window = window << 8 | block[Byte + i];
        return window >> Bit & MaxRaw;
    }

    public void WriteRaw(Span<byte> block, long raw)
    {
        if (raw < 0 || raw > MaxRaw) throw new ArgumentOutOfRangeException(nameof(raw), $"{Name}: {raw} does not fit in {Size} bit(s).");
        long window = 0;
        for (int i = 0; i < ByteSpan; i++) window = window << 8 | block[Byte + i];
        window = window & ~(MaxRaw << Bit) | raw << Bit;
        for (int i = ByteSpan - 1; i >= 0; i--) { block[Byte + i] = (byte)window; window >>= 8; }
    }

    public double ToValue(long raw) => raw * Multiplier + Offset;
    public long FromValue(double value) => (long)Math.Round((value - Offset) / Multiplier);

    /// <summary>Human readable value, e.g. "RVC Present", "14.5 seconds" or "'A'".</summary>
    public string Describe(long raw) => Kind switch
    {
        FieldKind.Enum => Options.TryGetValue(raw, out var s) ? s : $"Unknown (0x{raw:X})",
        FieldKind.Ascii => raw is >= 0x20 and < 0x7F ? $"'{(char)raw}'" : $"0x{raw:X2}",
        FieldKind.Hex => $"0x{raw:X2}",
        _ => ToValue(raw).ToString("0.###", CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(Unit) ? "" : " " + Unit),
    };
}

/// <summary>A configuration block: DID 0xDE00 + (Block - 1).</summary>
public sealed record CodingBlock(int Block, IReadOnlyList<CodingField> Fields, IReadOnlyList<int>? LineBytes = null, int MinSize = 0, bool LayoutGuessed = false)
{
    public ushort Did => BlockDids.ForBlock(Block);
}

public static class BlockDids
{
    /// <summary>As-built block N lives in data identifier DE00 + N - 1 (APIM: "Block 1 maps to 7D0-01 or DE00").</summary>
    public static ushort ForBlock(int block) => (ushort)(0xDE00 + block - 1);
    public static bool TryBlock(ushort did, out int block) { block = did - 0xDE00 + 1; return did is >= 0xDE00 and <= 0xDEFF; }
}
