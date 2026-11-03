using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ParquetNeo.Encodings;

/// <summary>
/// Packs and unpacks groups of 8 unsigned values of a fixed bit width (0 to 32), least significant bit
/// first, as used by the RLE/bit-packing hybrid encoding. A group of 8 values always occupies exactly
/// <c>bitWidth</c> bytes.
/// </summary>
internal static class BitPacking
{
    public const int MaxBitWidth = 32;

    /// <summary>Longest span an unaligned 8-byte read can extend past the end of a group's own bytes.</summary>
    private static int ReadSlack(int bitWidth) => (7 * bitWidth >> 3) + sizeof(ulong) - bitWidth;

    /// <summary>Unpacks <paramref name="destination"/>.Length values (a multiple of 8) from <paramref name="source"/>.</summary>
    public static void Unpack(ReadOnlySpan<byte> source, Span<uint> destination, int bitWidth)
    {
        ValidateWidth(bitWidth);
        if (destination.Length % 8 != 0)
        {
            throw new ArgumentException("The destination length must be a multiple of 8.", nameof(destination));
        }

        int groups = destination.Length / 8;
        if (source.Length < (long)groups * bitWidth)
        {
            throw new ArgumentException("The source is too short for the requested values.", nameof(source));
        }

        if (bitWidth == 0)
        {
            destination.Clear();
            return;
        }

        ulong mask = (1UL << bitWidth) - 1;
        int slack = ReadSlack(bitWidth);

        // Groups far enough from the end of the buffer can use 8-byte loads without reading out of bounds.
        int fastGroups = source.Length >= slack ? Math.Min(groups, (source.Length - slack) / bitWidth) : 0;
        ref byte src = ref MemoryMarshal.GetReference(source);
        ref uint dst = ref MemoryMarshal.GetReference(destination);
        int g = 0;
        for (; g < fastGroups; g++)
        {
            UnpackGroup(ref Unsafe.Add(ref src, g * bitWidth), ref Unsafe.Add(ref dst, g * 8), bitWidth, mask);
        }

        if (g < groups)
        {
            // The last few groups: copy into a zero-padded scratch buffer so the wide loads stay in bounds.
            Span<byte> scratch = stackalloc byte[MaxBitWidth + sizeof(ulong)];
            for (; g < groups; g++)
            {
                scratch.Clear();
                source.Slice(g * bitWidth, bitWidth).CopyTo(scratch);
                UnpackGroup(ref MemoryMarshal.GetReference(scratch), ref Unsafe.Add(ref dst, g * 8), bitWidth, mask);
            }
        }
    }

    /// <summary>Packs <paramref name="source"/> (a multiple of 8 values) into <paramref name="destination"/>.</summary>
    /// <remarks>Values must fit in <paramref name="bitWidth"/> bits; higher bits are discarded.</remarks>
    public static void Pack(ReadOnlySpan<uint> source, Span<byte> destination, int bitWidth)
    {
        ValidateWidth(bitWidth);
        if (source.Length % 8 != 0)
        {
            throw new ArgumentException("The source length must be a multiple of 8.", nameof(source));
        }

        int groups = source.Length / 8;
        if (destination.Length < (long)groups * bitWidth)
        {
            throw new ArgumentException("The destination is too short for the packed values.", nameof(destination));
        }

        if (bitWidth == 0)
        {
            return;
        }

        ulong mask = (1UL << bitWidth) - 1;
        for (int g = 0; g < groups; g++)
        {
            var values = source.Slice(g * 8, 8);
            var output = destination.Slice(g * bitWidth, bitWidth);
            ulong accumulator = 0;
            int bits = 0;
            int written = 0;
            for (int i = 0; i < 8; i++)
            {
                accumulator |= (values[i] & mask) << bits;
                bits += bitWidth;
                if (bits >= 32)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(written, 4), (uint)accumulator);
                    written += 4;
                    accumulator >>= 32;
                    bits -= 32;
                }
            }

            for (; bits > 0; bits -= 8)
            {
                output[written++] = (byte)accumulator;
                accumulator >>= 8;
            }
        }
    }

    /// <summary>Unpacks one group of 8 values; reads up to <see cref="ReadSlack"/> bytes past the group's own bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UnpackGroup(ref byte group, ref uint values, int bitWidth, ulong mask)
    {
        // Written out by hand: the JIT does not unroll a loop whose body depends on the runtime bit width.
        Unsafe.Add(ref values, 0) = Extract(ref group, 0, mask);
        Unsafe.Add(ref values, 1) = Extract(ref group, bitWidth, mask);
        Unsafe.Add(ref values, 2) = Extract(ref group, 2 * bitWidth, mask);
        Unsafe.Add(ref values, 3) = Extract(ref group, 3 * bitWidth, mask);
        Unsafe.Add(ref values, 4) = Extract(ref group, 4 * bitWidth, mask);
        Unsafe.Add(ref values, 5) = Extract(ref group, 5 * bitWidth, mask);
        Unsafe.Add(ref values, 6) = Extract(ref group, 6 * bitWidth, mask);
        Unsafe.Add(ref values, 7) = Extract(ref group, 7 * bitWidth, mask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Extract(ref byte group, int bit, ulong mask)
    {
        ulong word = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref group, bit >> 3));
        if (!BitConverter.IsLittleEndian)
        {
            word = BinaryPrimitives.ReverseEndianness(word);
        }

        return (uint)((word >> (bit & 7)) & mask);
    }

    private static void ValidateWidth(int bitWidth)
    {
        if ((uint)bitWidth > MaxBitWidth)
        {
            throw new ArgumentOutOfRangeException(nameof(bitWidth), bitWidth, "The bit width must be between 0 and 32.");
        }
    }
}
