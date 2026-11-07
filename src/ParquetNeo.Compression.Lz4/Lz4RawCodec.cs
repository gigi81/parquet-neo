using K4os.Compression.LZ4;
using ParquetNeo.Exceptions;
using ParquetNeo.Metadata;

namespace ParquetNeo.Compression.Lz4;

/// <summary>
/// LZ4_RAW: the plain LZ4 block format, using the managed K4os.Compression.LZ4 library. This is not the
/// deprecated, Hadoop-framed <c>LZ4</c> codec. Register it with <c>CodecRegistry.Default.AddLz4Raw()</c>.
/// </summary>
public sealed class Lz4RawCodec : ICodec
{
    private readonly LZ4Level _level;

    /// <param name="level">The default is the fast compressor; the high-compression levels are slower but smaller.</param>
    public Lz4RawCodec(LZ4Level level = LZ4Level.L00_FAST)
    {
        _level = level;
    }

    public CompressionCodec Id => CompressionCodec.Lz4Raw;

    public int GetMaxCompressedLength(int uncompressedLength) => LZ4Codec.MaximumOutputSize(uncompressedLength);

    public int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.IsEmpty)
        {
            // The canonical LZ4 block for no data is a single token with no literals and no match.
            destination[0] = 0;
            return 1;
        }

        int written = LZ4Codec.Encode(source, destination, _level);
        if (written < 0)
        {
            throw new ArgumentException("The destination is smaller than GetMaxCompressedLength.", nameof(destination));
        }

        return written;
    }

    public void Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (destination.IsEmpty)
        {
            // Nothing to decode: accept no bytes at all, or the canonical empty block (a lone zero token).
            if (source.IsEmpty || (source.Length == 1 && source[0] == 0))
            {
                return;
            }

            throw new CompressionException("LZ4 data is invalid or does not decompress to the 0 bytes its page header declares.");
        }

        int written = LZ4Codec.Decode(source, destination);
        if (written != destination.Length)
        {
            throw new CompressionException(
                $"LZ4 data is invalid or does not decompress to the {destination.Length} bytes its page header declares.");
        }
    }
}

public static class Lz4RawCodecRegistryExtensions
{
    public static CodecRegistry AddLz4Raw(this CodecRegistry registry, LZ4Level level = LZ4Level.L00_FAST) =>
        registry.Register(new Lz4RawCodec(level));
}
