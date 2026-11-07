using System.Buffers;
using System.Buffers.Binary;

namespace ParquetNeo.Encodings;

/// <summary>Decodes DELTA_LENGTH_BYTE_ARRAY data into PLAIN layout; see <see cref="DeltaLengthByteArrayEncoder"/>.</summary>
internal static class DeltaLengthByteArrayDecoder
{
    /// <summary>Appends the <paramref name="count"/> values of <paramref name="source"/> to <paramref name="plain"/> and returns the bytes of the source they used.</summary>
    /// <exception cref="ParquetFormatException">The data is malformed.</exception>
    public static int Decode(ReadOnlySpan<byte> source, int count, IBufferWriter<byte> plain)
    {
        var lengths = new int[count];
        int position = DeltaBinaryPackedDecoder.Decode(source, lengths);
        long total = 0;
        foreach (int length in lengths)
        {
            if (length < 0)
            {
                throw new ParquetFormatException("A delta length byte array has a negative length.");
            }

            total += length;
        }

        if (total > source.Length - position)
        {
            throw new ParquetFormatException($"The delta length byte arrays need {total} bytes but only {source.Length - position} follow their lengths.");
        }

        foreach (int length in lengths)
        {
            var destination = plain.GetSpan(sizeof(int) + length);
            BinaryPrimitives.WriteInt32LittleEndian(destination, length);
            source.Slice(position, length).CopyTo(destination.Slice(sizeof(int)));
            plain.Advance(sizeof(int) + length);
            position += length;
        }

        return position;
    }
}
