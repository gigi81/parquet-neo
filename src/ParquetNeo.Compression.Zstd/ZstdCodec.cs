using ParquetNeo.Exceptions;
using ZstdSharp;

namespace ParquetNeo.Compression.Zstd;

/// <summary>
/// ZSTD (RFC 8878) using the managed ZstdSharp.Port library. Register it with
/// <c>CodecRegistry.Default.AddZstd()</c>.
/// </summary>
/// <remarks>
/// Compression and decompression contexts are expensive to create and not thread safe, so each thread
/// keeps its own; dispose the codec to release them.
/// </remarks>
public sealed class ZstdCodec : ICodec, IDisposable
{
    private readonly ThreadLocal<Compressor> _compressors;
    private readonly ThreadLocal<Decompressor> _decompressors = new(() => new Decompressor(), trackAllValues: true);

    /// <param name="level">1 (fastest) to 22 (smallest); 3 is the zstd default.</param>
    public ZstdCodec(int level = 3)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 22);
        _compressors = new ThreadLocal<Compressor>(() => new Compressor(level), trackAllValues: true);
    }

    /// <summary>The codec this class implements: <see cref="CompressionCodec.Zstd"/>.</summary>
    public CompressionCodec Id => CompressionCodec.Zstd;

    /// <inheritdoc />
    public int GetMaxCompressedLength(int uncompressedLength) => Compressor.GetCompressBound(uncompressedLength);

    /// <inheritdoc />
    public int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (destination.Length < GetMaxCompressedLength(source.Length))
        {
            throw new ArgumentException("The destination is smaller than GetMaxCompressedLength.", nameof(destination));
        }

        return _compressors.Value!.Wrap(source, destination);
    }

    /// <inheritdoc />
    public void Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.IsEmpty && destination.IsEmpty)
        {
            return;
        }

        int written;
        try
        {
            written = _decompressors.Value!.Unwrap(source, destination);
        }
        catch (ZstdException exception)
        {
            throw new CompressionException($"Invalid ZSTD data: {exception.Message}", exception);
        }

        if (written != destination.Length)
        {
            throw new CompressionException(
                $"ZSTD data decompresses to {written} bytes but the page header declares {destination.Length}.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var compressor in _compressors.Values)
        {
            compressor.Dispose();
        }

        foreach (var decompressor in _decompressors.Values)
        {
            decompressor.Dispose();
        }

        _compressors.Dispose();
        _decompressors.Dispose();
    }
}

/// <summary>Registers the Zstandard codec of <c>ParquetNeo.Compression.Zstd</c> with a <see cref="CodecRegistry"/>.</summary>
public static class ZstdCodecRegistryExtensions
{
    /// <summary>Registers a <see cref="ZstdCodec"/> that lives as long as the process (or the registry).</summary>
    public static CodecRegistry AddZstd(this CodecRegistry registry, int level = 3) => registry.Register(new ZstdCodec(level));
}
