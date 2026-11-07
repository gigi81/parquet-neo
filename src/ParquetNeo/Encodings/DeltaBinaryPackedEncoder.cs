using System.Buffers;
using System.Numerics;

namespace ParquetNeo.Encodings;

/// <summary>
/// DELTA_BINARY_PACKED: the first value, then the differences between neighbours, each block of them stored relative to
/// its smallest difference in as few bits as the largest remainder needs. Good for values that change little or steadily.
/// </summary>
/// <remarks>
/// Differences use the arithmetic of the value type, wrapping on overflow, so every INT32 remainder fits 32 bits and
/// every INT64 remainder 64 bits. A block holds 128 values in 4 miniblocks of 32, as most writers do.
/// </remarks>
internal static class DeltaBinaryPackedEncoder
{
    public const int BlockSize = 128;
    public const int MiniblocksPerBlock = 4;
    public const int MiniblockSize = BlockSize / MiniblocksPerBlock;

    public static void Encode(ReadOnlySpan<int> values, IBufferWriter<byte> output) => Encode<int>(values, output);

    public static void Encode(ReadOnlySpan<long> values, IBufferWriter<byte> output) => Encode<long>(values, output);

    private static void Encode<T>(ReadOnlySpan<T> values, IBufferWriter<byte> output)
        where T : unmanaged, IBinaryInteger<T>
    {
        VariableLengthInteger.WriteUnsigned(BlockSize, output);
        VariableLengthInteger.WriteUnsigned(MiniblocksPerBlock, output);
        VariableLengthInteger.WriteUnsigned((ulong)values.Length, output);
        VariableLengthInteger.WriteZigZag(values.IsEmpty ? 0 : long.CreateTruncating(values[0]), output);

        Span<long> deltas = stackalloc long[BlockSize];
        Span<ulong> remainders = stackalloc ulong[BlockSize];
        Span<byte> widths = stackalloc byte[MiniblocksPerBlock];
        Span<byte> packed = stackalloc byte[MiniblockSize * sizeof(ulong)];
        for (int start = 1; start < values.Length; start += BlockSize)
        {
            int inBlock = Math.Min(BlockSize, values.Length - start);
            long minimum = long.MaxValue;
            for (int i = 0; i < inBlock; i++)
            {
                // The difference wraps in the width of the type; widening an int keeps its sign, so the minimum compares right.
                T difference = values[start + i] - values[start + i - 1];
                long delta = long.CreateTruncating(difference);
                deltas[i] = delta;
                minimum = Math.Min(minimum, delta);
            }

            for (int i = 0; i < BlockSize; i++)
            {
                // Both are within the range of the type (sign extended), so the remainder fits its width.
                remainders[i] = i < inBlock ? unchecked((ulong)(deltas[i] - minimum)) : 0;
            }

            int miniblocksUsed = (inBlock + MiniblockSize - 1) / MiniblockSize;
            for (int m = 0; m < MiniblocksPerBlock; m++)
            {
                widths[m] = m < miniblocksUsed ? (byte)BitWidth(remainders.Slice(m * MiniblockSize, MiniblockSize)) : (byte)0;
            }

            VariableLengthInteger.WriteZigZag(minimum, output);
            output.Write(widths);
            for (int m = 0; m < miniblocksUsed; m++)
            {
                int bytes = MiniblockSize * widths[m] / 8;
                WideBitPacking.Pack(remainders.Slice(m * MiniblockSize, MiniblockSize), widths[m], packed);
                output.Write(packed[..bytes]);
            }
        }
    }

    private static int BitWidth(ReadOnlySpan<ulong> values)
    {
        ulong union = 0;
        foreach (ulong value in values)
        {
            union |= value;
        }

        return 64 - BitOperations.LeadingZeroCount(union);
    }
}
