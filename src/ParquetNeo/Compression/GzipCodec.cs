using System.Diagnostics.CodeAnalysis;
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
[SuppressMessage("Major Code Smell", "S6640:Using unsafe code blocks is security-sensitive",
    Justification = "UnmanagedMemoryStream wraps the caller's spans so that GZipStream reads and writes them in place; the alternative is copying every page through a managed buffer. The pointers are pinned by 'fixed' for the length of the call and never escape.")]
public sealed unsafe class GzipCodec : ICodec
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
        if (destination.Length < GetMaxCompressedLength(source.Length))
        {
            throw new ArgumentException("The destination is smaller than GetMaxCompressedLength.", nameof(destination));
        }

        if (source.IsEmpty)
        {
            // GZipStream writes nothing at all for no data, but other readers expect a valid (empty) member.
            EmptyMember.CopyTo(destination);
            return EmptyMember.Length;
        }

        fixed (byte* target = destination)
        {
            // The stream writes straight into the destination and fails if it would overflow it.
            using var output = new UnmanagedMemoryStream(target, 0, destination.Length, FileAccess.Write);
            try
            {
                using var gzip = new GZipStream(output, _level, leaveOpen: true);
                gzip.Write(source);
            }
            catch (NotSupportedException)
            {
                throw new InvalidOperationException(
                    $"GZIP output exceeded the {destination.Length}-byte buffer for {source.Length} input bytes; " +
                    "GetMaxCompressedLength was not a true upper bound.");
            }

            return checked((int)output.Position);
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

        try
        {
            fixed (byte* input = source)
            {
                using var stream = new UnmanagedMemoryStream(input, source.Length);
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
        }
        catch (InvalidDataException exception)
        {
            throw new CompressionException($"Invalid GZIP data: {exception.Message}", exception);
        }
    }
}
