using System.IO.Compression;
using ParquetNeo.Exceptions;

namespace ParquetNeo.Compression;

/// <summary>Brotli (RFC 7932), via the span-based <see cref="BrotliEncoder"/> and <see cref="BrotliDecoder"/>.</summary>
public sealed class BrotliCodec : ICodec
{
    private const int DefaultWindowBits = 22;

    private readonly int _quality;

    /// <param name="quality">0 (fastest) to 11 (smallest); 4 is the .NET default.</param>
    public BrotliCodec(int quality = 4)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(quality, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quality, 11);
        _quality = quality;
    }

    public CompressionCodec Id => CompressionCodec.Brotli;

    public int GetMaxCompressedLength(int uncompressedLength) => BrotliEncoder.GetMaxCompressedLength(uncompressedLength);

    public int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (!BrotliEncoder.TryCompress(source, destination, out int written, _quality, DefaultWindowBits))
        {
            throw new ArgumentException("The destination is smaller than GetMaxCompressedLength.", nameof(destination));
        }

        return written;
    }

    public void Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.IsEmpty && destination.IsEmpty)
        {
            return;
        }

        // False means invalid data, or output larger than the destination.
        if (!BrotliDecoder.TryDecompress(source, destination, out int written) || written != destination.Length)
        {
            throw new CompressionException(
                $"Brotli data is invalid or does not decompress to the {destination.Length} bytes its page header declares.");
        }
    }
}
