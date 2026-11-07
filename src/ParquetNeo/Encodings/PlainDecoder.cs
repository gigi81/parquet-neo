using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ParquetNeo.Exceptions;

namespace ParquetNeo.Encodings;

/// <summary>
/// Decoders for the PLAIN encoding. Each method fills the destination with exactly
/// <c>destination.Length</c> values and returns the number of bytes consumed.
/// </summary>
/// <exception cref="EncodingException">All methods: the data is too short or malformed.</exception>
internal static class PlainDecoder
{
    /// <summary>For each byte value, its 8 bits as 8 bytes of 0 or 1 (bit 0 first), in memory order.</summary>
    private static readonly ulong[] BitExpansion = CreateBitExpansion();

    public static int ReadInt32s(ReadOnlySpan<byte> source, Span<int> destination)
    {
        int bytes = Take(source, destination.Length, sizeof(int));
        if (BitConverter.IsLittleEndian)
        {
            MemoryMarshal.Cast<byte, int>(source[..bytes]).CopyTo(destination);
        }
        else
        {
            for (int i = 0; i < destination.Length; i++)
            {
                destination[i] = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(i * sizeof(int)));
            }
        }

        return bytes;
    }

    public static int ReadInt64s(ReadOnlySpan<byte> source, Span<long> destination)
    {
        int bytes = Take(source, destination.Length, sizeof(long));
        if (BitConverter.IsLittleEndian)
        {
            MemoryMarshal.Cast<byte, long>(source[..bytes]).CopyTo(destination);
        }
        else
        {
            for (int i = 0; i < destination.Length; i++)
            {
                destination[i] = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(i * sizeof(long)));
            }
        }

        return bytes;
    }

    public static int ReadSingles(ReadOnlySpan<byte> source, Span<float> destination)
    {
        int bytes = Take(source, destination.Length, sizeof(float));
        if (BitConverter.IsLittleEndian)
        {
            MemoryMarshal.Cast<byte, float>(source[..bytes]).CopyTo(destination);
        }
        else
        {
            for (int i = 0; i < destination.Length; i++)
            {
                destination[i] = BinaryPrimitives.ReadSingleLittleEndian(source.Slice(i * sizeof(float)));
            }
        }

        return bytes;
    }

    public static int ReadDoubles(ReadOnlySpan<byte> source, Span<double> destination)
    {
        int bytes = Take(source, destination.Length, sizeof(double));
        if (BitConverter.IsLittleEndian)
        {
            MemoryMarshal.Cast<byte, double>(source[..bytes]).CopyTo(destination);
        }
        else
        {
            for (int i = 0; i < destination.Length; i++)
            {
                destination[i] = BinaryPrimitives.ReadDoubleLittleEndian(source.Slice(i * sizeof(double)));
            }
        }

        return bytes;
    }

    public static int ReadInt96s(ReadOnlySpan<byte> source, Span<Int96> destination)
    {
        const int size = 12;
        int bytes = Take(source, destination.Length, size);
        for (int i = 0; i < destination.Length; i++)
        {
            var value = source.Slice(i * size, size);
            destination[i] = new Int96(
                BinaryPrimitives.ReadInt64LittleEndian(value),
                BinaryPrimitives.ReadInt32LittleEndian(value.Slice(8)));
        }

        return bytes;
    }

    /// <summary>Unpacks bit-packed booleans (least significant bit first).</summary>
    public static int ReadBooleans(ReadOnlySpan<byte> source, Span<bool> destination)
    {
        int bytes = (int)(((long)destination.Length + 7) / 8);
        if (source.Length < bytes)
        {
            throw TooShort(bytes, source.Length);
        }

        var output = MemoryMarshal.AsBytes(destination);
        int i = 0;

        // One table lookup and one 8-byte store expand a whole source byte into 8 booleans.
        ref ulong expansion = ref MemoryMarshal.GetArrayDataReference(BitExpansion);
        for (; i + 8 <= output.Length; i += 8)
        {
            Unsafe.WriteUnaligned(ref output[i], Unsafe.Add(ref expansion, source[i >> 3]));
        }

        for (; i < output.Length; i++)
        {
            output[i] = (byte)((source[i >> 3] >> (i & 7)) & 1);
        }

        return bytes;
    }

    /// <summary>
    /// Locates length-prefixed BYTE_ARRAY values. <see cref="ByteRange.Offset"/> is relative to
    /// <paramref name="source"/>, so the values themselves are never copied.
    /// </summary>
    public static int ReadByteArrays(ReadOnlySpan<byte> source, Span<ByteRange> destination)
    {
        int position = 0;
        for (int i = 0; i < destination.Length; i++)
        {
            if (source.Length - position < sizeof(int))
            {
                throw new EncodingException($"PLAIN byte array {i} has no length prefix: the data ends.");
            }

            int length = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(position));
            position += sizeof(int);
            if ((uint)length > (uint)(source.Length - position))
            {
                throw new EncodingException(
                    $"PLAIN byte array {i} declares {length} bytes but only {source.Length - position} remain.");
            }

            destination[i] = new ByteRange(position, length);
            position += length;
        }

        return position;
    }

    /// <summary>Returns the <paramref name="count"/> values of <paramref name="length"/> bytes each, back to back.</summary>
    public static ReadOnlySpan<byte> ReadFixedLengthByteArrays(ReadOnlySpan<byte> source, int count, int length)
    {
        int bytes = Take(source, count, length);
        return source[..bytes];
    }

    private static ulong[] CreateBitExpansion()
    {
        var table = new ulong[256];
        Span<byte> bytes = stackalloc byte[8];
        for (int value = 0; value < table.Length; value++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                bytes[bit] = (byte)((value >> bit) & 1);
            }

            // A native-order read, so that a native-order store later reproduces exactly these bytes.
            table[value] = Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(bytes));
        }

        return table;
    }

    /// <summary>Checks that <paramref name="count"/> values of <paramref name="size"/> bytes are available and returns their total size.</summary>
    private static int Take(ReadOnlySpan<byte> source, int count, int size)
    {
        long bytes = (long)count * size;
        if (bytes > source.Length)
        {
            throw TooShort(bytes, source.Length);
        }

        return (int)bytes;
    }

    private static EncodingException TooShort(long needed, int available) =>
        new($"PLAIN data is too short: {needed} bytes are needed but only {available} are available.");
}
