using System.Globalization;

namespace IndexedLargeDiskData;

/// <summary>Hexadecimal text for the 64-bit values this library stores.</summary>
/// <remarks>
/// Every value on a record is a <see cref="ulong"/>, and the ones that matter here — identifiers,
/// block numbers, packed fields — are far easier to read, compare and paste as hex than as fifteen
/// digits of decimal. The default rendering is fixed width and zero padded so that hex text sorts in
/// the same order as the numbers it stands for; that is what makes it safe to use as a key or a
/// sortable label rather than only as something to print.
/// </remarks>
public static class Hex
{
    /// <summary>The number of characters in a fixed-width rendering of a <see cref="ulong"/>.</summary>
    public const int Width = 16;

    private const string Prefix = "0x";

    /// <summary>Renders <paramref name="value"/> as <see cref="Width"/> lowercase hex characters.</summary>
    /// <param name="value">The value to render.</param>
    /// <returns>Exactly <see cref="Width"/> characters, zero padded on the left.</returns>
    /// <example>
    /// <code>
    /// Hex.ToHexString(255) == "00000000000000ff"
    /// </code>
    /// </example>
    public static string ToHexString(ulong value) => value.ToString("x16", CultureInfo.InvariantCulture);

    /// <summary>Renders <paramref name="value"/> as lowercase hex, padded or trimmed.</summary>
    /// <param name="value">The value to render.</param>
    /// <param name="padded">
    /// True for the fixed <see cref="Width"/> that sorts in numeric order; false for the shortest
    /// text that round-trips, which reads better in a message but no longer sorts.
    /// </param>
    /// <returns>Hex characters, without a <c>0x</c> prefix.</returns>
    public static string ToHexString(ulong value, bool padded)
    {
        if (padded)
        {
            return ToHexString(value);
        }
        else
        {
            return value.ToString("x", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Parses hex text back into a <see cref="ulong"/>.</summary>
    /// <param name="text">
    /// One to <see cref="Width"/> hex digits in either case, optionally led by <c>0x</c>. Leading
    /// zeros are allowed, so anything <see cref="ToHexString(ulong)"/> produced parses back.
    /// </param>
    /// <returns>The value the text stands for.</returns>
    /// <exception cref="FormatException"><paramref name="text"/> is not hex that fits in 64 bits.</exception>
    public static ulong Parse(ReadOnlySpan<char> text)
    {
        if (TryParse(text, out ulong value))
        {
            return value;
        }

        throw new FormatException($"'{text}' is not a hexadecimal 64-bit value.");
    }

    /// <summary>Parses hex text back into a <see cref="ulong"/> without throwing.</summary>
    /// <param name="text">The text to parse, as described on <see cref="Parse"/>.</param>
    /// <param name="value">The parsed value, or zero when the text does not parse.</param>
    /// <returns>True when <paramref name="text"/> parsed.</returns>
    /// <remarks>
    /// Hand-rolled rather than <see cref="ulong.TryParse(ReadOnlySpan{char}, NumberStyles, IFormatProvider, out ulong)"/>
    /// because <see cref="NumberStyles.HexNumber"/> accepts surrounding whitespace and rejects the
    /// <c>0x</c> prefix, which is the opposite of what is wanted for text that came from a file, a
    /// command line or a log.
    /// </remarks>
    public static bool TryParse(ReadOnlySpan<char> text, out ulong value)
    {
        value = 0;

        if (text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            text = text[Prefix.Length..];
        }

        // An empty span would otherwise parse as zero, and more than Width digits cannot be told
        // apart from a value that overflowed.
        if (text.IsEmpty || text.Length > Width)
        {
            return false;
        }

        ulong result = 0;
        foreach (char c in text)
        {
            int digit = DigitOf(c);
            if (digit < 0)
            {
                return false;
            }

            result = (result << 4) | (uint)digit;
        }

        value = result;
        return true;
    }

    /// <summary>Gets the value of one hex digit, or -1 when <paramref name="c"/> is not one.</summary>
    private static int DigitOf(char c)
    {
        if (c is >= '0' and <= '9')
        {
            return c - '0';
        }
        else if (c is >= 'a' and <= 'f')
        {
            return c - 'a' + 10;
        }
        else if (c is >= 'A' and <= 'F')
        {
            return c - 'A' + 10;
        }
        else
        {
            return -1;
        }
    }
}
