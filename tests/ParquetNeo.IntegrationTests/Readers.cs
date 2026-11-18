using ParquetNeo.Compression;
using ParquetNeo.Compression.Lz4;
using ParquetNeo.Compression.Snappy;
using ParquetNeo.Compression.Zstd;

namespace ParquetNeo.IntegrationTests;

/// <summary>The settings ParquetNeo reads the files of Python with: every codec, and page checksums checked when the file has them.</summary>
internal static class Readers
{
    public static readonly CodecRegistry Codecs = new CodecRegistry().AddSnappy().AddZstd().AddLz4Raw();

    public static ParquetReaderOptions Options { get; } = new() { Codecs = Codecs, VerifyPageChecksums = true };

    public static ParquetWriterOptions Writer(ParquetWriterOptions? options = null) => (options ?? new ParquetWriterOptions()) with { Codecs = Codecs };
}
