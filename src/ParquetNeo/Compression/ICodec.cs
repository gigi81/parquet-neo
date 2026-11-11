using ParquetNeo.Exceptions;

namespace ParquetNeo.Compression;

/// <summary>
/// A page compression codec. Parquet stores the uncompressed and compressed size of every page, so both
/// directions work on buffers of known size and never need to grow or stream.
/// </summary>
/// <remarks>Implementations must be thread safe: one instance serves all readers and writers that use it.</remarks>
public interface ICodec
{
    /// <summary>The Parquet codec this implementation handles.</summary>
    CompressionCodec Id { get; }

    /// <summary>An upper bound on the compressed size of <paramref name="uncompressedLength"/> bytes.</summary>
    int GetMaxCompressedLength(int uncompressedLength);

    /// <summary>
    /// Compresses <paramref name="source"/> into <paramref name="destination"/>, which must hold at least
    /// <see cref="GetMaxCompressedLength"/> bytes, and returns the number of bytes written.
    /// </summary>
    int Compress(ReadOnlySpan<byte> source, Span<byte> destination);

    /// <summary>
    /// Decompresses <paramref name="source"/> into <paramref name="destination"/>, whose length must be exactly
    /// the uncompressed size recorded in the page header.
    /// </summary>
    /// <exception cref="CompressionException">
    /// The data is corrupt, or does not decompress to exactly <c>destination.Length</c> bytes.
    /// </exception>
    void Decompress(ReadOnlySpan<byte> source, Span<byte> destination);
}
