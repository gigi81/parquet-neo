using System.Buffers;
using System.Buffers.Binary;

namespace ParquetNeo.Encodings;

/// <summary>
/// DELTA_LENGTH_BYTE_ARRAY: the lengths of all values as DELTA_BINARY_PACKED INT32s, then the bytes of all values back to
/// back. Good for byte arrays of similar length, and compresses well because the bytes sit together.
/// </summary>
internal static class DeltaLengthByteArrayEncoder
{
    /// <param name="plain">The values in PLAIN layout: each a 4-byte length and its bytes.</param>
    /// <param name="count">The number of values in <paramref name="plain"/>.</param>
    public static void Encode(ReadOnlySpan<byte> plain, int count, IBufferWriter<byte> output)
    {
        var lengths = new int[count];
        int position = 0;
        for (int i = 0; i < count; i++)
        {
            lengths[i] = BinaryPrimitives.ReadInt32LittleEndian(plain.Slice(position));
            position += sizeof(int) + lengths[i];
        }

        DeltaBinaryPackedEncoder.Encode(lengths, output);
        position = 0;
        for (int i = 0; i < count; i++)
        {
            output.Write(plain.Slice(position + sizeof(int), lengths[i]));
            position += sizeof(int) + lengths[i];
        }
    }
}
