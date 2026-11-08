namespace ParquetNeo.Metadata;

public enum CompressionCodec
{
    Uncompressed = 0,
    Snappy = 1,
    Gzip = 2,
    Lzo = 3,
    Brotli = 4,
    /// <summary>Deprecated Hadoop-framed LZ4; use <see cref="Lz4Raw"/>.</summary>
    Lz4 = 5,
    Zstd = 6,
    Lz4Raw = 7,
}