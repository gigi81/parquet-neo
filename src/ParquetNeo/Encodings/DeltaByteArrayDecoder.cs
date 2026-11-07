using System.Buffers;
using System.Buffers.Binary;

namespace ParquetNeo.Encodings;

/// <summary>Decodes DELTA_BYTE_ARRAY data; see <see cref="DeltaByteArrayEncoder"/>.</summary>
internal static class DeltaByteArrayDecoder
{
    /// <summary>
    /// Appends the <paramref name="count"/> values to <paramref name="plain"/>: in PLAIN layout for BYTE_ARRAY, or back to
    /// back for FIXED_LEN_BYTE_ARRAY (<paramref name="fixedLength"/> above 0, which every value must have).
    /// </summary>
    /// <param name="maxOutputBytes">The most bytes the values may add up to: shared prefixes let a few bytes of input describe a lot of output.</param>
    /// <exception cref="ParquetFormatException">The data is malformed, or the values would be longer than the limit.</exception>
    public static void Decode(ReadOnlySpan<byte> source, int count, int fixedLength, long maxOutputBytes, IBufferWriter<byte> plain)
    {
        var prefixes = new int[count];
        int position = DeltaBinaryPackedDecoder.Decode(source, prefixes);
        var suffixes = new ArrayBufferWriter<byte>();
        DeltaLengthByteArrayDecoder.Decode(source.Slice(position), count, suffixes);

        var suffixData = suffixes.WrittenSpan;
        var previous = new byte[0];
        int suffixPosition = 0;
        long written = 0;
        for (int i = 0; i < count; i++)
        {
            int suffixLength = BinaryPrimitives.ReadInt32LittleEndian(suffixData.Slice(suffixPosition));
            var suffix = suffixData.Slice(suffixPosition + sizeof(int), suffixLength);
            suffixPosition += sizeof(int) + suffixLength;
            int prefix = prefixes[i];
            if (prefix < 0 || prefix > previous.Length)
            {
                throw new ParquetFormatException($"A delta byte array shares {prefix} bytes with the previous value, which has {previous.Length}.");
            }

            long length = (long)prefix + suffixLength;
            written += length + (fixedLength > 0 ? 0 : sizeof(int));
            if (written > maxOutputBytes)
            {
                throw new ParquetFormatException($"The delta byte arrays add up to more than the limit of {maxOutputBytes} bytes.");
            }

            if (fixedLength > 0 && length != fixedLength)
            {
                throw new ParquetFormatException($"A fixed-length delta byte array has {length} bytes, not {fixedLength}.");
            }

            var value = new byte[length];
            previous.AsSpan(0, prefix).CopyTo(value);
            suffix.CopyTo(value.AsSpan(prefix));
            if (fixedLength == 0)
            {
                PlainEncoder.WriteByteArray(value, plain);
            }
            else
            {
                plain.Write(value);
            }

            previous = value;
        }
    }
}
