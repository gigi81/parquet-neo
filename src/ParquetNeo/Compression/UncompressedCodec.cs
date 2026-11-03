using ParquetNeo.Metadata;

namespace ParquetNeo.Compression;

/// <summary>The "no compression" codec: data is copied as it is.</summary>
public sealed class UncompressedCodec : ICodec
{
    public CompressionCodec Id => CompressionCodec.Uncompressed;

    public int GetMaxCompressedLength(int uncompressedLength) => uncompressedLength;

    public int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        source.CopyTo(destination);
        return source.Length;
    }

    public void Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.Length != destination.Length)
        {
            throw new ParquetFormatException(
                $"An uncompressed page holds {source.Length} bytes but its header says {destination.Length}.");
        }

        source.CopyTo(destination);
    }
}
