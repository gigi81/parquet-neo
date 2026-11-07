using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Runtime.Intrinsics;

namespace ParquetNeo.Encodings;

/// <summary>Encoders for the PLAIN encoding, appending to an <see cref="IBufferWriter{T}"/>.</summary>
internal static class PlainEncoder
{
    public static void WriteInt32s(ReadOnlySpan<int> values, IBufferWriter<byte> output)
    {
        if (BitConverter.IsLittleEndian)
        {
            WriteBytes(MemoryMarshal.AsBytes(values), output);
            return;
        }

        var span = output.GetSpan(values.Length * sizeof(int));
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(i * sizeof(int)), values[i]);
        }

        output.Advance(values.Length * sizeof(int));
    }

    public static void WriteInt64s(ReadOnlySpan<long> values, IBufferWriter<byte> output)
    {
        if (BitConverter.IsLittleEndian)
        {
            WriteBytes(MemoryMarshal.AsBytes(values), output);
            return;
        }

        var span = output.GetSpan(values.Length * sizeof(long));
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(i * sizeof(long)), values[i]);
        }

        output.Advance(values.Length * sizeof(long));
    }

    public static void WriteSingles(ReadOnlySpan<float> values, IBufferWriter<byte> output)
    {
        if (BitConverter.IsLittleEndian)
        {
            WriteBytes(MemoryMarshal.AsBytes(values), output);
            return;
        }

        var span = output.GetSpan(values.Length * sizeof(float));
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(span.Slice(i * sizeof(float)), values[i]);
        }

        output.Advance(values.Length * sizeof(float));
    }

    public static void WriteDoubles(ReadOnlySpan<double> values, IBufferWriter<byte> output)
    {
        if (BitConverter.IsLittleEndian)
        {
            WriteBytes(MemoryMarshal.AsBytes(values), output);
            return;
        }

        var span = output.GetSpan(values.Length * sizeof(double));
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(i * sizeof(double)), values[i]);
        }

        output.Advance(values.Length * sizeof(double));
    }

    public static void WriteInt96s(ReadOnlySpan<Int96> values, IBufferWriter<byte> output)
    {
        const int size = 12;
        var span = output.GetSpan(values.Length * size);
        for (int i = 0; i < values.Length; i++)
        {
            var target = span.Slice(i * size, size);
            BinaryPrimitives.WriteInt64LittleEndian(target, values[i].NanosOfDay);
            BinaryPrimitives.WriteInt32LittleEndian(target.Slice(8), values[i].JulianDay);
        }

        output.Advance(values.Length * size);
    }

    /// <summary>Packs booleans least significant bit first; the last byte is padded with zero bits.</summary>
    public static void WriteBooleans(ReadOnlySpan<bool> values, IBufferWriter<byte> output)
    {
        int bytes = (int)(((long)values.Length + 7) / 8);
        var span = output.GetSpan(bytes);
        var input = MemoryMarshal.AsBytes(values);

        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            // Booleans are 0 or 1, so the top bit of (value << 7) is the value; its mask is already LSB first.
            for (; i + 16 <= input.Length; i += 16)
            {
                var lanes = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(input), (nuint)i);
                uint mask = Vector128.ExtractMostSignificantBits(Vector128.ShiftLeft(lanes, 7));
                span[i >> 3] = (byte)mask;
                span[(i >> 3) + 1] = (byte)(mask >> 8);
            }
        }

        if (i < input.Length)
        {
            span.Slice(i >> 3, bytes - (i >> 3)).Clear();
            for (; i < input.Length; i++)
            {
                span[i >> 3] |= (byte)((input[i] & 1) << (i & 7));
            }
        }

        output.Advance(bytes);
    }

    /// <summary>Appends one string as a BYTE_ARRAY value, UTF-8 encoded straight into the output.</summary>
    public static void WriteString(string value, IBufferWriter<byte> output)
    {
        int length = Encoding.UTF8.GetByteCount(value);
        var span = output.GetSpan(sizeof(int) + length);
        BinaryPrimitives.WriteInt32LittleEndian(span, length);
        Encoding.UTF8.GetBytes(value, span.Slice(sizeof(int)));
        output.Advance(sizeof(int) + length);
    }

    /// <summary>Appends one value: a 4-byte little-endian length followed by the bytes.</summary>
    public static void WriteByteArray(ReadOnlySpan<byte> value, IBufferWriter<byte> output)
    {
        var span = output.GetSpan(sizeof(int) + value.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span, value.Length);
        value.CopyTo(span.Slice(sizeof(int)));
        output.Advance(sizeof(int) + value.Length);
    }

    /// <summary>Appends the values back to back; each must be exactly <paramref name="length"/> bytes.</summary>
    public static void WriteFixedLengthByteArray(ReadOnlySpan<byte> value, int length, IBufferWriter<byte> output)
    {
        if (value.Length != length)
        {
            throw new ArgumentException($"The value has {value.Length} bytes but the column's length is {length}.", nameof(value));
        }

        WriteBytes(value, output);
    }

    private static void WriteBytes(ReadOnlySpan<byte> bytes, IBufferWriter<byte> output)
    {
        bytes.CopyTo(output.GetSpan(bytes.Length));
        output.Advance(bytes.Length);
    }
}
