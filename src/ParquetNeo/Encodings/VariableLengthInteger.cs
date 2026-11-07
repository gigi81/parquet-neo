using ParquetNeo.Exceptions;
namespace ParquetNeo.Encodings;

/// <summary>The variable-length integers of the delta encodings: unsigned LEB128, and zigzag for signed values.</summary>
internal static class VariableLengthInteger
{
    /// <summary>Reads an unsigned LEB128 value and moves <paramref name="position"/> past it.</summary>
    /// <exception cref="EncodingException">The data ends inside the value or the value is longer than 64 bits.</exception>
    public static ulong ReadUnsigned(ReadOnlySpan<byte> source, ref int position)
    {
        ulong result = 0;
        for (int shift = 0; shift < 70; shift += 7)
        {
            if (position >= source.Length)
            {
                throw new EncodingException("A variable-length integer is cut off by the end of the data.");
            }

            byte next = source[position++];
            if (shift == 63 && next > 1)
            {
                throw new EncodingException("A variable-length integer does not fit in 64 bits.");
            }

            result |= (ulong)(next & 0x7F) << shift;
            if ((next & 0x80) == 0)
            {
                return result;
            }
        }

        throw new EncodingException("A variable-length integer is longer than 10 bytes.");
    }

    public static long ReadZigZag(ReadOnlySpan<byte> source, ref int position)
    {
        ulong value = ReadUnsigned(source, ref position);
        return (long)(value >> 1) ^ -(long)(value & 1);
    }

    public static void WriteUnsigned(ulong value, System.Buffers.IBufferWriter<byte> output)
    {
        var span = output.GetSpan(10);
        int length = 0;
        while (value >= 0x80)
        {
            span[length++] = (byte)(value | 0x80);
            value >>= 7;
        }

        span[length++] = (byte)value;
        output.Advance(length);
    }

    public static void WriteZigZag(long value, System.Buffers.IBufferWriter<byte> output) =>
        WriteUnsigned((ulong)((value << 1) ^ (value >> 63)), output);
}
