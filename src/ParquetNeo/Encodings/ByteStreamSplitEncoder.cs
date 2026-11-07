using System.Buffers;

namespace ParquetNeo.Encodings;

/// <summary>
/// BYTE_STREAM_SPLIT: the first bytes of all values, then the second bytes, and so on. It does not shrink the data, but
/// puts like bytes together so that a general-purpose compressor does far better on floating-point numbers.
/// </summary>
internal static class ByteStreamSplitEncoder
{
    /// <param name="plain">The values back to back, <paramref name="valueWidth"/> bytes each.</param>
    public static void Encode(ReadOnlySpan<byte> plain, int valueWidth, IBufferWriter<byte> output)
    {
        int count = plain.Length / valueWidth;
        var destination = output.GetSpan(plain.Length);
        for (int value = 0; value < count; value++)
        {
            for (int part = 0; part < valueWidth; part++)
            {
                destination[part * count + value] = plain[value * valueWidth + part];
            }
        }

        output.Advance(plain.Length);
    }
}
