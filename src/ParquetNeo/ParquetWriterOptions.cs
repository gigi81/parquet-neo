using ParquetNeo.Compression;

namespace ParquetNeo;

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

    /// <summary>
    /// Whether columns are dictionary encoded: each distinct value is stored once and the rows refer to it by number,
    /// which is far smaller for repetitive data. A column falls back to plain encoding once its dictionary grows past
    /// <see cref="DictionaryPageSizeLimit"/>. Booleans are never dictionary encoded. Defaults to <c>true</c>.
    /// </summary>
    public bool DictionaryEnabled { get; init; } = true;

    /// <summary>
    /// How chosen columns are dictionary encoded, keyed by column path with dots (<c>scores.list.element</c>): the
    /// <see cref="DictionaryMode"/> and the size limit of the dictionary, in place of <see cref="DictionaryPageSizeLimit"/>. The
    /// others use <see cref="DictionaryMode.Standard"/> and the limit of the writer. A <see cref="DictionaryMode"/> can be given
    /// where settings are expected. Nothing here makes a column use a dictionary when <see cref="DictionaryEnabled"/> is off.
    /// </summary>
    /// <exception cref="ArgumentException">Reported when the writer is created: a column that does not exist, or a size limit below 1.</exception>
    public IReadOnlyDictionary<string, DictionarySettings>? ColumnDictionaries { get; init; }

    /// <summary>
    /// The size, in bytes, a column chunk's dictionary may reach before the writer stops adding to it and falls back to
    /// plain encoding for the rest of the chunk. Defaults to 1 MiB. The size is checked after every page worth of
    /// values (and after every batch written), so a dictionary can overshoot the limit by up to that much.
    /// </summary>
    public int DictionaryPageSizeLimit { get; init; } = 1024 * 1024;

    /// <summary>
    /// Whether the minimum, maximum and null count of every column chunk are recorded in the footer, which lets readers
    /// skip data that cannot match a filter. Costs a pass over the values. Defaults to <c>true</c>.
    /// </summary>
    public bool StatisticsEnabled { get; init; } = true;

    /// <summary>
    /// Whether the header of every page carries the CRC-32 of the page as it is written (after compression), so that a reader can
    /// tell that the bytes were damaged. Computing it costs about 1 second per GB of page bytes: noticeable (tens of percent) when
    /// writing uncompressed pages, negligible next to a compression codec. Off by default.
    /// </summary>
    public bool WritePageChecksums { get; init; }

    /// <summary>
    /// The columns that get a bloom filter in every row group, keyed by column path with dots (<c>address.city</c>). A bloom
    /// filter tells a reader that a value is certainly not in a row group (it is wrong the other way a small share of the time, see
    /// <see cref="BloomFilterSettings.FalsePositiveRate"/>), which the minimum and the maximum cannot when the values are spread
    /// widely, as ids and names are. Each takes room in the file, about a byte for every few different values. Hashing and counting
    /// the values makes writing the column several times slower when the pages are not compressed, and little slower when they are.
    /// Booleans and INT96 cannot have one.
    /// </summary>
    /// <exception cref="ArgumentException">Reported when the writer is created: a column that does not exist or cannot have a filter, or settings that make no sense.</exception>
    public IReadOnlyDictionary<string, BloomFilterSettings>? ColumnBloomFilters { get; init; }

    /// <summary>
    /// The longest minimum or maximum, in bytes, kept for string and binary columns. Longer ones are shortened to a
    /// bound (a prefix for a minimum, an increased prefix for a maximum) and marked as not exact. Defaults to 64.
    /// </summary>
    public int StatisticsTruncateLength { get; init; } = 64;

    /// <summary>
    /// The encoding to use for the values of chosen columns, keyed by column path with dots (<c>scores.list.element</c>).
    /// The default is PLAIN. A column chosen here uses its encoding for every page that is not dictionary encoded: all of
    /// them when <see cref="DictionaryEnabled"/> is off, otherwise the pages after the dictionary is given up.
    /// DELTA_BINARY_PACKED suits integers that change steadily, DELTA_LENGTH_BYTE_ARRAY and DELTA_BYTE_ARRAY strings
    /// (the latter when they share prefixes), and BYTE_STREAM_SPLIT floating-point numbers before compression.
    /// </summary>
    /// <exception cref="ArgumentException">Reported when the writer is created: a column that does not exist, or an encoding its type cannot use.</exception>
    public IReadOnlyDictionary<string, ParquetEncoding>? ColumnEncodings { get; init; }

    /// <summary>
    /// The layout of the data pages. Defaults to <see cref="DataPageVersion.V1"/>, which every reader understands;
    /// <see cref="DataPageVersion.V2"/> stores the levels uncompressed and compresses only the values.
    /// </summary>
    public DataPageVersion DataPageVersion { get; init; } = DataPageVersion.V1;

    /// <summary>Free-form key/value pairs stored in the file footer.</summary>
    public IReadOnlyDictionary<string, string>? KeyValueMetadata { get; init; }

    /// <summary>The writer name stored in the footer; defaults to "ParquetNeo version x.y.z".</summary>
    public string? CreatedBy { get; init; }
}
