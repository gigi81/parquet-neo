using System.IO.Compression;
using ParquetNeo.Metadata;

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
public sealed unsafe class GzipCodec : ICodec
{
    private readonly CompressionLevel _level;

    public GzipCodec(CompressionLevel level = CompressionLevel.Optimal)
    {
        _level = level;
    }

    public CompressionCodec Id => CompressionCodec.Gzip;

    /// <summary>Stored-block worst case plus the 18 bytes of gzip header and trailer, with margin.</summary>
    public int GetMaxCompressedLength(int uncompressedLength)
    {
        long bound = uncompressedLength + (uncompressedLength >> 8) + 64L;
        return bound > int.MaxValue ? throw new ArgumentOutOfRangeException(nameof(uncompressedLength)) : (int)bound;
    }

    public int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (destination.Length < GetMaxCompressedLength(source.Length))
        {
            throw new ArgumentException("The destination is smaller than GetMaxCompressedLength.", nameof(destination));
        }

        fixed (byte* target = destination)
        {
            // The stream writes straight into the destination and fails if it would overflow it.
            using var output = new UnmanagedMemoryStream(target, 0, destination.Length, FileAccess.Write);
            using (var gzip = new GZipStream(output, _level, leaveOpen: true))
            {
                gzip.Write(source);
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

            throw new ParquetFormatException("A GZIP page is empty but its header expects data.");
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
                    throw new ParquetFormatException(
                        $"GZIP data does not decompress to the {destination.Length} bytes its page header declares.");
                }
            }
        }
        catch (InvalidDataException exception)
        {
            throw new ParquetFormatException($"Invalid GZIP data: {exception.Message}");
        }
    }
}
