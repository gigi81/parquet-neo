namespace ParquetNeo.Encodings;

/// <summary>Decodes DELTA_BINARY_PACKED data; see <see cref="DeltaBinaryPackedEncoder"/>.</summary>
internal static class DeltaBinaryPackedDecoder
{
    /// <summary>The largest block size accepted: a hostile header cannot ask for more memory than this.</summary>
    private const int MaxBlockSize = 1 << 16;

    /// <summary>Decodes exactly <paramref name="destination"/>.Length INT32 values and returns the number of bytes used.</summary>
    /// <exception cref="ParquetFormatException">The data is malformed, or holds a different number of values.</exception>
    public static int Decode(ReadOnlySpan<byte> source, Span<int> destination)
    {
        var wide = destination.Length <= 1024 ? stackalloc long[destination.Length] : new long[destination.Length];
        int used = Decode(source, wide, 32);
        for (int i = 0; i < wide.Length; i++)
        {
            destination[i] = (int)wide[i];
        }

        return used;
    }

    /// <summary>Decodes exactly <paramref name="destination"/>.Length INT64 values and returns the number of bytes used.</summary>
    public static int Decode(ReadOnlySpan<byte> source, Span<long> destination) => Decode(source, destination, 64);

    private static int Decode(ReadOnlySpan<byte> source, Span<long> destination, int width)
    {
        int position = 0;
        ulong blockSize = VariableLengthInteger.ReadUnsigned(source, ref position);
        ulong miniblocks = VariableLengthInteger.ReadUnsigned(source, ref position);
        ulong count = VariableLengthInteger.ReadUnsigned(source, ref position);
        long first = VariableLengthInteger.ReadZigZag(source, ref position);
        if (blockSize == 0 || blockSize > MaxBlockSize || blockSize % 128 != 0 || miniblocks == 0 || blockSize % miniblocks != 0 || (blockSize / miniblocks) % 32 != 0)
        {
            throw new ParquetFormatException($"A delta header has an invalid layout: block size {blockSize}, {miniblocks} miniblocks.");
        }

        if (count != (ulong)destination.Length)
        {
            throw new ParquetFormatException($"A delta header declares {count} values but the page holds {destination.Length}.");
        }

        if (destination.IsEmpty)
        {
            return position;
        }

        destination[0] = first;
        int miniblockSize = (int)(blockSize / miniblocks);
        Span<ulong> remainders = new ulong[miniblockSize];
        Span<byte> widths = stackalloc byte[(int)Math.Min(miniblocks, 256)];
        if ((int)miniblocks > widths.Length)
        {
            throw new ParquetFormatException("A delta header declares too many miniblocks.");
        }

        widths = widths[..(int)miniblocks];
        long previous = destination[0];
        int decoded = 1;
        while (decoded < destination.Length)
        {
            long minimum = VariableLengthInteger.ReadZigZag(source, ref position);
            if (source.Length - position < widths.Length)
            {
                throw new ParquetFormatException("A delta block is cut off before its miniblock widths.");
            }

            source.Slice(position, widths.Length).CopyTo(widths);
            position += widths.Length;
            for (int m = 0; m < widths.Length && decoded < destination.Length; m++)
            {
                int bitWidth = widths[m];
                if (bitWidth > width)
                {
                    throw new ParquetFormatException($"A delta miniblock has a bit width of {bitWidth}, more than the {width} bits of its type.");
                }

                int bytes = checked(miniblockSize * bitWidth / 8);
                if (source.Length - position < bytes)
                {
                    throw new ParquetFormatException("A delta miniblock is cut off by the end of the data.");
                }

                WideBitPacking.Unpack(source.Slice(position, bytes), bitWidth, remainders);
                position += bytes;
                for (int i = 0; i < miniblockSize && decoded < destination.Length; i++, decoded++)
                {
                    // The sum wraps in 64 bits; for INT32 the caller keeps the low 32 bits, which is the same as wrapping there.
                    previous = unchecked(previous + (long)remainders[i] + minimum);
                    destination[decoded] = previous;
                }
            }
        }

        return position;
    }
}
