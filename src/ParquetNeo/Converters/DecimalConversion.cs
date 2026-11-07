using ParquetNeo.Exceptions;

namespace ParquetNeo.Converters;

/// <summary>
/// The arithmetic shared by the DECIMAL converters. A decimal column stores the <i>unscaled</i> integer, so that the value
/// is <c>unscaled / 10^scale</c>; <see cref="decimal"/> holds 96 bits of it and a scale of up to 28.
/// </summary>
internal static class DecimalConversion
{
    /// <summary>The largest scale a <see cref="decimal"/> can have.</summary>
    public const int MaxScale = 28;

    private static readonly UInt128[] PowersOfTen = CreatePowersOfTen();

    /// <summary>The value of an unscaled integer that fits 64 bits.</summary>
    public static decimal FromInt64(long unscaled, int scale)
    {
        ulong magnitude = unscaled < 0 ? 0UL - (ulong)unscaled : (ulong)unscaled;
        return new decimal((int)magnitude, (int)(magnitude >> 32), 0, unscaled < 0, (byte)scale);
    }

    /// <exception cref="ValueConversionException">The unscaled integer needs more than the 96 bits a decimal holds.</exception>
    public static decimal FromInt128(Int128 unscaled, int scale)
    {
        UInt128 magnitude = unscaled < 0 ? UInt128.Zero - (UInt128)unscaled : (UInt128)unscaled;
        if ((magnitude >> 96) != 0)
        {
            throw new ValueConversionException("A decimal value is too large for a .NET decimal, which holds 96 bits.");
        }

        return new decimal((int)(uint)magnitude, (int)(uint)(magnitude >> 32), (int)(uint)(magnitude >> 64), unscaled < 0, (byte)scale);
    }

    /// <summary>The value of an unscaled integer stored as big-endian two's complement bytes, of any length.</summary>
    /// <exception cref="ValueConversionException">The bytes are empty or hold a number too large for a .NET decimal.</exception>
    public static decimal FromBigEndian(ReadOnlySpan<byte> bytes, int scale)
    {
        if (bytes.IsEmpty)
        {
            throw new ValueConversionException("A decimal value has no bytes.");
        }

        bool negative = (sbyte)bytes[0] < 0;
        if (bytes.Length > 16)
        {
            // Only sign extension may sit above the 16 bytes an Int128 holds.
            byte extension = negative ? (byte)0xFF : (byte)0;
            if (bytes[..^16].ContainsAnyExcept(extension) || ((sbyte)bytes[^16] < 0) != negative)
            {
                throw new ValueConversionException("A decimal value is too large for a .NET decimal, which holds 96 bits.");
            }

            bytes = bytes[^16..];
        }

        Int128 unscaled = negative ? Int128.NegativeOne : Int128.Zero;
        foreach (byte b in bytes)
        {
            unscaled = (unscaled << 8) | b;
        }

        return FromInt128(unscaled, scale);
    }

    /// <summary>
    /// The unscaled integer of <paramref name="value"/> at the column's scale. Trailing zeros are added or removed as needed,
    /// but digits are never lost: a value with more decimals than the scale holds is refused.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value has non-zero digits beyond the scale, more digits than the precision, or needs more than 127 bits unscaled.
    /// </exception>
    public static Int128 ToUnscaled(decimal value, int scale, int precision)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        int valueScale = (bits[3] >> 16) & 0xFF;
        bool negative = bits[3] < 0;
        UInt128 magnitude = ((UInt128)(uint)bits[2] << 64) | ((UInt128)(uint)bits[1] << 32) | (uint)bits[0];

        if (valueScale < scale)
        {
            try
            {
                magnitude = checked(magnitude * PowersOfTen[scale - valueScale]);
            }
            catch (OverflowException)
            {
                throw TooManyDigits(value, scale);
            }
        }
        else if (valueScale > scale)
        {
            var (quotient, remainder) = UInt128.DivRem(magnitude, PowersOfTen[valueScale - scale]);
            if (remainder != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, $"The value has more decimals than the scale of {scale} holds, so storing it would lose digits.");
            }

            magnitude = quotient;
        }

        if (precision <= 38 ? magnitude >= PowersOfTen[precision] : (magnitude >> 127) != 0)
        {
            throw TooManyDigits(value, scale, precision);
        }

        return negative ? -(Int128)magnitude : (Int128)magnitude;
    }

    /// <summary>Writes an unscaled integer as big-endian two's complement into all of <paramref name="destination"/>, sign extended.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The integer does not fit in the destination.</exception>
    public static void WriteBigEndian(Int128 unscaled, Span<byte> destination)
    {
        if (destination.Length < 16)
        {
            Int128 limit = Int128.One << ((8 * destination.Length) - 1);
            if (unscaled < -limit || unscaled >= limit)
            {
                throw new ArgumentOutOfRangeException(nameof(unscaled), $"The value does not fit in {destination.Length} bytes.");
            }
        }

        byte extension = unscaled < 0 ? (byte)0xFF : (byte)0;
        for (int i = 0; i < destination.Length; i++)
        {
            int shift = 8 * i;
            destination[^(i + 1)] = shift >= 128 ? extension : (byte)(unscaled >> shift);
        }
    }

    /// <summary>The number of bytes (1 to 16) the shortest two's complement form of an integer takes.</summary>
    public static int MinimalLength(Int128 unscaled)
    {
        for (int length = 1; length < 16; length++)
        {
            Int128 limit = Int128.One << ((8 * length) - 1);
            if (unscaled >= -limit && unscaled < limit)
            {
                return length;
            }
        }

        return 16;
    }

    private static ArgumentOutOfRangeException TooManyDigits(decimal value, int scale, int? precision = null) =>
        new(nameof(value), value, precision is null
            ? $"The value needs more digits than a decimal column with a scale of {scale} can hold."
            : $"The value has more digits than the precision of {precision} allows at a scale of {scale}.");

    private static UInt128[] CreatePowersOfTen()
    {
        var powers = new UInt128[39];
        powers[0] = UInt128.One;
        for (int i = 1; i < powers.Length; i++)
        {
            powers[i] = powers[i - 1] * 10;
        }

        return powers;
    }
}
