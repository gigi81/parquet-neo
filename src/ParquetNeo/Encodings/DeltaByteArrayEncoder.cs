using System.Buffers;
using System.Buffers.Binary;

namespace ParquetNeo.Encodings;

/// <summary>
/// DELTA_BYTE_ARRAY: each value as the length of the prefix it shares with the value before it (DELTA_BINARY_PACKED), then
/// the remaining suffixes (DELTA_LENGTH_BYTE_ARRAY). Good for sorted or similar strings such as URLs and file paths.
/// </summary>
internal static class DeltaByteArrayEncoder
{
    /// <param name="plain">The values: PLAIN layout for BYTE_ARRAY, or back to back for FIXED_LEN_BYTE_ARRAY.</param>
    /// <param name="fixedLength">The length of every value of a FIXED_LEN_BYTE_ARRAY column, or 0 for BYTE_ARRAY.</param>
    public static void Encode(ReadOnlySpan<byte> plain, int count, int fixedLength, IBufferWriter<byte> output)
    {
        var prefixes = new int[count];
        var suffixes = new ArrayBufferWriter<byte>();
        ReadOnlySpan<byte> previous = default;
        int position = 0;
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> value;
            if (fixedLength > 0)
            {
                value = plain.Slice(position, fixedLength);
                position += fixedLength;
            }
            else
            {
                int length = BinaryPrimitives.ReadInt32LittleEndian(plain.Slice(position));
                value = plain.Slice(position + sizeof(int), length);
                position += sizeof(int) + length;
            }

            int prefix = value.CommonPrefixLength(previous);
            prefixes[i] = prefix;
            PlainEncoder.WriteByteArray(value.Slice(prefix), suffixes);
            previous = value;
        }

        DeltaBinaryPackedEncoder.Encode(prefixes, output);
        DeltaLengthByteArrayEncoder.Encode(suffixes.WrittenSpan, count, output);
    }
}
