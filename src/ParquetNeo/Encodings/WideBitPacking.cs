namespace ParquetNeo.Encodings;

/// <summary>Packs and unpacks runs of values of any bit width up to 64, least significant bit first.</summary>
internal static class WideBitPacking
{
    /// <summary>Packs <paramref name="values"/> into <c>values.Length * bitWidth / 8</c> bytes (the count must make that whole).</summary>
    public static void Pack(ReadOnlySpan<ulong> values, int bitWidth, Span<byte> destination)
    {
        if (bitWidth == 0)
        {
            return;
        }

        UInt128 accumulator = 0;
        int bits = 0;
        int written = 0;
        foreach (ulong value in values)
        {
            accumulator |= (UInt128)value << bits;
            bits += bitWidth;
            while (bits >= 8)
            {
                destination[written++] = (byte)accumulator;
                accumulator >>= 8;
                bits -= 8;
            }
        }
    }

    /// <summary>Unpacks <paramref name="destination"/>.Length values from <paramref name="source"/>, which must hold enough bytes.</summary>
    public static void Unpack(ReadOnlySpan<byte> source, int bitWidth, Span<ulong> destination)
    {
        if (bitWidth == 0)
        {
            destination.Clear();
            return;
        }

        ulong mask = bitWidth == 64 ? ulong.MaxValue : (1UL << bitWidth) - 1;
        UInt128 accumulator = 0;
        int bits = 0;
        int read = 0;
        for (int i = 0; i < destination.Length; i++)
        {
            while (bits < bitWidth)
            {
                accumulator |= (UInt128)source[read++] << bits;
                bits += 8;
            }

            destination[i] = (ulong)accumulator & mask;
            accumulator >>= bitWidth;
            bits -= bitWidth;
        }
    }
}
