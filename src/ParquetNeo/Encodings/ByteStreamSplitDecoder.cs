using ParquetNeo.Exceptions;
namespace ParquetNeo.Encodings;

/// <summary>Decodes BYTE_STREAM_SPLIT data; see <see cref="ByteStreamSplitEncoder"/>.</summary>
internal static class ByteStreamSplitDecoder
{
    /// <summary>Writes the <paramref name="count"/> values back to back into <paramref name="plain"/>, which must hold <c>count * valueWidth</c> bytes.</summary>
    /// <exception cref="EncodingException">The source does not hold that many bytes.</exception>
    public static void Decode(ReadOnlySpan<byte> source, int count, int valueWidth, Span<byte> plain)
    {
        long needed = (long)count * valueWidth;
        if (source.Length < needed)
        {
            throw new EncodingException($"{count} byte-stream-split values of {valueWidth} bytes need {needed} bytes but the page has {source.Length}.");
        }

        for (int value = 0; value < count; value++)
        {
            for (int part = 0; part < valueWidth; part++)
            {
                plain[value * valueWidth + part] = source[part * count + value];
            }
        }
    }
}
