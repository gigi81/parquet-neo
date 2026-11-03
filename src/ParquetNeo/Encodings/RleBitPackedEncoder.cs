using System.Buffers;
using System.Runtime.InteropServices;

namespace ParquetNeo.Encodings;

/// <summary>
/// Encoder for the RLE/bit-packing hybrid encoding. Runs of at least 8 equal values become RLE runs;
/// everything else is bit-packed in groups of 8. The output is the encoded data only: a 4-byte length
/// prefix, where the page type needs one, is added by the caller.
/// </summary>
internal static class RleBitPackedEncoder
{
    /// <summary>Longest bit-packed run written, in groups of 8 values; bounds the buffer requested per run.</summary>
    private const int MaxGroupsPerRun = 8192;

    /// <summary>The shortest repetition worth an RLE run.</summary>
    private const int MinRepeatedRun = 8;

    /// <exception cref="ArgumentException">A value does not fit in <paramref name="bitWidth"/> bits.</exception>
    public static void Encode(ReadOnlySpan<int> values, int bitWidth, IBufferWriter<byte> output)
    {
        if ((uint)bitWidth > BitPacking.MaxBitWidth)
        {
            throw new ArgumentOutOfRangeException(nameof(bitWidth), bitWidth, "The bit width must be between 0 and 32.");
        }

        var data = MemoryMarshal.Cast<int, uint>(values);
        EnsureValuesFit(data, bitWidth);

        int index = 0;
        int literalStart = 0; // start of the values waiting to be bit-packed
        while (index < data.Length)
        {
            uint value = data[index];
            int run = 1;
            if (index + 1 < data.Length && data[index + 1] == value)
            {
                int different = data.Slice(index + 1).IndexOfAnyExcept(value);
                run = different < 0 ? data.Length - index : different + 1;
            }

            if (run >= MinRepeatedRun)
            {
                // A bit-packed run must hold a multiple of 8 values, so the repeated value first tops
                // up the waiting literals; the RLE run then takes whatever is left.
                int literalCount = index - literalStart;
                int topUp = -literalCount & 7;
                if (run - topUp >= MinRepeatedRun)
                {
                    int literalEnd = index + topUp;
                    if (literalEnd > literalStart)
                    {
                        WriteBitPacked(data[literalStart..literalEnd], bitWidth, output);
                    }

                    WriteRepeated(value, run - topUp, bitWidth, output);
                    literalStart = index + run;
                }
            }

            index += run;
        }

        if (literalStart < data.Length)
        {
            WriteBitPacked(data[literalStart..], bitWidth, output); // the last group is zero-padded
        }
    }

    private static void EnsureValuesFit(ReadOnlySpan<uint> values, int bitWidth)
    {
        if (bitWidth == 32)
        {
            return;
        }

        uint combined = 0;
        foreach (uint value in values)
        {
            combined |= value;
        }

        if (combined >> bitWidth != 0)
        {
            throw new ArgumentException($"A value does not fit in {bitWidth} bits (values must be non-negative).", nameof(values));
        }
    }

    private static void WriteRepeated(uint value, int count, int bitWidth, IBufferWriter<byte> output)
    {
        int valueBytes = (bitWidth + 7) / 8;
        var span = output.GetSpan(5 + valueBytes);
        int length = WriteVarint32(span, (uint)count << 1);
        for (int i = 0; i < valueBytes; i++)
        {
            span[length++] = (byte)(value >> (8 * i));
        }

        output.Advance(length);
    }

    /// <summary>Writes the values as bit-packed runs; a final partial group is padded with zeros.</summary>
    private static void WriteBitPacked(ReadOnlySpan<uint> values, int bitWidth, IBufferWriter<byte> output)
    {
        Span<uint> last = stackalloc uint[8];
        while (values.Length > 0)
        {
            int chunkValues = Math.Min(values.Length, MaxGroupsPerRun * 8);
            var chunk = values[..chunkValues];
            values = values[chunkValues..];

            int groups = (chunkValues + 7) / 8;
            int packedBytes = groups * bitWidth;
            var span = output.GetSpan(5 + packedBytes);
            int length = WriteVarint32(span, ((uint)groups << 1) | 1);

            int wholeValues = chunkValues & ~7;
            BitPacking.Pack(chunk[..wholeValues], span.Slice(length), bitWidth);
            if (wholeValues < chunkValues)
            {
                last.Clear();
                chunk[wholeValues..].CopyTo(last);
                BitPacking.Pack(last, span.Slice(length + wholeValues / 8 * bitWidth), bitWidth);
            }

            output.Advance(length + packedBytes);
        }
    }

    private static int WriteVarint32(Span<byte> destination, uint value)
    {
        int length = 0;
        while (value >= 0x80)
        {
            destination[length++] = (byte)(value | 0x80);
            value >>= 7;
        }

        destination[length++] = (byte)value;
        return length;
    }
}
