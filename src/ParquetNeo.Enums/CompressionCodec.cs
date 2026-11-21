namespace ParquetNeo;

/// <summary>The compression codec of the pages of a column chunk (thrift <c>CompressionCodec</c>).</summary>
public enum CompressionCodec
{
    /// <summary>The pages are not compressed.</summary>
    Uncompressed = 0,
    /// <summary>Snappy (package <c>ParquetNeo.Compression.Snappy</c>).</summary>
    Snappy = 1,
    /// <summary>Gzip, built in.</summary>
    Gzip = 2,
    /// <summary>LZO, which this library does not implement; a file that uses it cannot be read.</summary>
    Lzo = 3,
    /// <summary>Brotli, built in.</summary>
    Brotli = 4,
    /// <summary>Deprecated Hadoop-framed LZ4; use <see cref="Lz4Raw"/>.</summary>
    Lz4 = 5,
    /// <summary>Zstandard (package <c>ParquetNeo.Compression.Zstd</c>).</summary>
    Zstd = 6,
    /// <summary>LZ4 blocks without framing (package <c>ParquetNeo.Compression.Lz4</c>).</summary>
    Lz4Raw = 7,
}