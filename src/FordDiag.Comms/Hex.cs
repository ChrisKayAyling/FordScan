using System.Globalization;

namespace FordDiag.Comms;

/// <summary>Hex helpers (space/whitespace tolerant parsing, upper-case formatting).</summary>
public static class Hex
{
    /// <summary>Parses "10 C0", "10c0" etc. Throws <see cref="FormatException"/> on bad input.</summary>
    public static byte[] Parse(string text)
    {
        if (!TryParse(text, out var bytes)) throw new FormatException($"Invalid hex string '{text}'.");
        return bytes;
    }

    /// <summary>Tries to parse a hex string ignoring whitespace. Empty input yields an empty array.</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out byte[] bytes)
    {
        Span<char> clean = text.Length <= 512 ? stackalloc char[text.Length] : new char[text.Length];
        int n = 0;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) continue;
            clean[n++] = c;
        }
        bytes = Array.Empty<byte>();
        if ((n & 1) != 0) return false;
        var result = new byte[n / 2];
        for (int i = 0; i < result.Length; i++)
        {
            if (!byte.TryParse(clean.Slice(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result[i]))
                return false;
        }
        bytes = result;
        return true;
    }

    /// <summary>True when every non-space char is a hex digit (and at least one is present).</summary>
    public static bool IsHexLine(ReadOnlySpan<char> text)
    {
        int digits = 0;
        foreach (var c in text)
        {
            if (c == ' ') continue;
            if (!Uri.IsHexDigit(c)) return false;
            digits++;
        }
        return digits > 0;
    }

    /// <summary>Formats bytes as upper-case hex, optionally separated by spaces.</summary>
    public static string ToString(ReadOnlySpan<byte> data, bool spaced = false)
    {
        if (data.IsEmpty) return string.Empty;
        if (!spaced) return Convert.ToHexString(data);
        return string.Create(data.Length * 3 - 1, data.ToArray(), static (span, d) =>
        {
            const string digits = "0123456789ABCDEF";
            for (int i = 0; i < d.Length; i++)
            {
                int p = i * 3;
                span[p] = digits[d[i] >> 4];
                span[p + 1] = digits[d[i] & 15];
                if (i < d.Length - 1) span[p + 2] = ' ';
            }
        });
    }
}
