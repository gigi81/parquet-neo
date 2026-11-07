using ParquetNeo.Compression;

namespace ParquetNeo.Core;

/// <summary>Settings for a <see cref="ParquetFileReader"/>. The limits protect against corrupt or hostile files.</summary>
public sealed record ParquetReaderOptions
{
    /// <summary>Where compression codecs are looked up. Defaults to <see cref="CodecRegistry.Default"/>.</summary>
    public CodecRegistry Codecs { get; init; } = CodecRegistry.Default;

    /// <summary>The largest footer that is read, in bytes. Defaults to 256 MiB.</summary>
    public int MaxFooterSize { get; init; } = 256 * 1024 * 1024;

    /// <summary>The largest uncompressed page that is read, in bytes. Defaults to 256 MiB.</summary>
    public int MaxPageSize { get; init; } = 256 * 1024 * 1024;

    /// <summary>
    /// The largest number of rows a single page may hold. A page of mostly nulls can be tiny on disk but needs
    /// memory for its levels while it is read. Defaults to 16,777,216.
    /// </summary>
    public int MaxRowsPerPage { get; init; } = 1 << 24;
}
