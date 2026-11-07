using ParquetNeo.Exceptions;
using ParquetNeo.Metadata;
using SnappierCodec = Snappier.Snappy;

namespace ParquetNeo.Compression.Snappy;

/// <summary>
/// Snappy in its raw block format (no framing), which is what Parquet pages use, using the managed Snappier
/// library. Register it with <c>CodecRegistry.Default.AddSnappy()</c>.
/// </summary>
public sealed class SnappyCodec : ICodec
{
    /// <summary>Beyond this the worst-case compressed size would overflow an <see cref="int"/>.</summary>
    private const int MaxUncompressedLength = 1_800_000_000;

    public CompressionCodec Id => CompressionCodec.Snappy;

    public int GetMaxCompressedLength(int uncompressedLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(uncompressedLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(uncompressedLength, MaxUncompressedLength);
        return SnappierCodec.GetMaxCompressedLength(uncompressedLength);
    }

    public int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (destination.Length < GetMaxCompressedLength(source.Length))
        {
            throw new ArgumentException("The destination is smaller than GetMaxCompressedLength.", nameof(destination));
        }

        return SnappierCodec.Compress(source, destination);
    }

    public void Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.IsEmpty && destination.IsEmpty)
        {
            return; // tolerated: some writers emit nothing for an empty page
        }

        try
        {
            // Snappier accepts a destination larger than the data, so the declared size is checked up front.
            int declared = SnappierCodec.GetUncompressedLength(source);
            if (declared != destination.Length)
            {
                throw new CompressionException(
                    $"Snappy data declares {declared} uncompressed bytes but the page header says {destination.Length}.");
            }

            int written = SnappierCodec.Decompress(source, destination);
            if (written != destination.Length)
            {
                throw new CompressionException(
                    $"Snappy data decodes to {written} bytes but declares {destination.Length}.");
            }
        }
        catch (InvalidDataException exception)
        {
            throw new CompressionException($"Invalid Snappy data: {exception.Message}", exception);
        }
    }
}

public static class SnappyCodecRegistryExtensions
{
    public static CodecRegistry AddSnappy(this CodecRegistry registry) => registry.Register(new SnappyCodec());
}
