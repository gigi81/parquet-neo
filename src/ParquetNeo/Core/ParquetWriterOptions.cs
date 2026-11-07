using ParquetNeo.Compression;
using ParquetNeo.Metadata;

namespace ParquetNeo.Core;

/// <summary>Settings for a <see cref="ParquetFileWriter"/>.</summary>
public sealed record ParquetWriterOptions
{
    /// <summary>
    /// How pages are compressed. The codec must be available in <see cref="Codecs"/>; Snappy, Zstd and LZ4_RAW
    /// come from add-on packages and have to be registered first. Defaults to no compression.
    /// </summary>
    public CompressionCodec Compression { get; init; } = CompressionCodec.Uncompressed;

    /// <summary>Where the codec is looked up. Defaults to <see cref="CodecRegistry.Default"/>.</summary>
    public CodecRegistry Codecs { get; init; } = CodecRegistry.Default;

    /// <summary>
    /// The size, in bytes of encoded values before compression, at which a data page is closed. Smaller pages
    /// allow finer-grained reading, larger ones spend less on headers. A single value bigger than this still
    /// gets a page of its own. Defaults to 1 MiB.
    /// </summary>
    public int DataPageSize { get; init; } = 1024 * 1024;

    /// <summary>Free-form key/value pairs stored in the file footer.</summary>
    public IReadOnlyDictionary<string, string>? KeyValueMetadata { get; init; }

    /// <summary>The writer name stored in the footer; defaults to "ParquetNeo version x.y.z".</summary>
    public string? CreatedBy { get; init; }
}
