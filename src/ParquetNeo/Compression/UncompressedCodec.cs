using ParquetNeo.Exceptions;

namespace ParquetNeo.Compression;

/// <summary>The "no compression" codec: data is copied as it is.</summary>
public sealed class UncompressedCodec : ICodec
{
    /// <summary>The codec this class implements: <see cref="CompressionCodec.Uncompressed"/>.</summary>
    public CompressionCodec Id => CompressionCodec.Uncompressed;

    /// <inheritdoc />
    public int GetMaxCompressedLength(int uncompressedLength) => uncompressedLength;

    /// <inheritdoc />
    public int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        source.CopyTo(destination);
        return source.Length;
    }

    /// <inheritdoc />
    public void Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.Length != destination.Length)
        {
            throw new CompressionException(
                $"An uncompressed page holds {source.Length} bytes but its header says {destination.Length}.");
        }

        source.CopyTo(destination);
    }
}
