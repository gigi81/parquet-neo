using System.Buffers;
using System.IO.Compression;
using ParquetNeo.Exceptions;

namespace ParquetNeo.Compression;

/// <summary>
/// GZIP (RFC 1952), via <see cref="GZipStream"/>. Decompression accepts several members back to back, as the
/// spec asks of readers.
/// </summary>
/// <remarks>
/// Corrupt data, including a wrong CRC or size, is reported by the BCL inflater. It stops reading as soon as the
/// deflate stream ends though, so a member that was cut off inside its 8-byte trailer still yields all of its
/// data and is accepted. Parquet's optional per-page CRC32 is the integrity check for that.
/// </remarks>
public sealed class GzipCodec : ICodec
{
    /// <summary>A gzip member holding no data: header, an empty final deflate block, CRC32 0 and size 0.</summary>
    private static ReadOnlySpan<byte> EmptyMember =>
        [0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0xFF, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    private readonly CompressionLevel _level;

    public GzipCodec(CompressionLevel level = CompressionLevel.Optimal)
    {
        _level = level;
    }

    public CompressionCodec Id => CompressionCodec.Gzip;

    /// <summary>
    /// The worst case of any DEFLATE encoder: fixed-Huffman blocks spend 9 bits on half of all byte values, so
    /// incompressible data grows by up to 12.5%. Real encoders usually fall back to stored blocks and stay near
    /// 0.03%, but zlib-ng's fastest level does not (about 5.5% growth on random data), so the bound is the
    /// theoretical one. The 64 bytes cover the gzip header and trailer and per-block overhead.
    /// </summary>
    public int GetMaxCompressedLength(int uncompressedLength)
    {
        long bound = uncompressedLength + (uncompressedLength >> 3) + 64L;
        return bound > int.MaxValue ? throw new ArgumentOutOfRangeException(nameof(uncompressedLength)) : (int)bound;
    }

    public int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int bound = GetMaxCompressedLength(source.Length);
        if (destination.Length < bound)
        {
            throw new ArgumentException("The destination is smaller than GetMaxCompressedLength.", nameof(destination));
        }

        if (source.IsEmpty)
        {
            // GZipStream writes nothing at all for no data, but other readers expect a valid (empty) member.
            EmptyMember.CopyTo(destination);
            return EmptyMember.Length;
        }

        // A stream cannot hold the destination span, so GZipStream writes into a pooled array of the maximum length and the
        // compressed bytes, a fraction of the page, are copied to the destination: about 1% of the time of compressing.
        var buffer = ArrayPool<byte>.Shared.Rent(bound);
        try
        {
            // A stream over an array has a fixed size: it fails, instead of growing, if the output would overflow it.
            using var output = new MemoryStream(buffer, 0, bound, writable: true);
            try
            {
                using var gzip = new GZipStream(output, _level, leaveOpen: true);
                gzip.Write(source);
            }
            catch (NotSupportedException)
            {
                throw new InvalidOperationException(
                    $"GZIP output exceeded the {bound}-byte buffer for {source.Length} input bytes; " +
                    "GetMaxCompressedLength was not a true upper bound.");
            }

            int length = checked((int)output.Position);
            buffer.AsSpan(0, length).CopyTo(destination);
            return length;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.IsEmpty)
        {
            if (destination.IsEmpty)
            {
                return; // tolerated: some writers emit nothing for an empty page
            }

            throw new CompressionException("A GZIP page is empty but its header expects data.");
        }

        // A stream cannot hold the source span either: the compressed page is copied to a pooled array that the stream reads, and
        // GZipStream decompresses straight into the destination.
        var buffer = ArrayPool<byte>.Shared.Rent(source.Length);
        try
        {
            source.CopyTo(buffer);
            using var stream = new MemoryStream(buffer, 0, source.Length, writable: false);
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);

            int total = 0;
            while (total < destination.Length)
            {
                int read = gzip.Read(destination.Slice(total));
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (total != destination.Length || gzip.ReadByte() != -1)
            {
                throw new CompressionException(
                    $"GZIP data does not decompress to the {destination.Length} bytes its page header declares.");
            }
        }
        catch (InvalidDataException exception)
        {
            throw new CompressionException($"Invalid GZIP data: {exception.Message}", exception);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
