using ParquetNeo.Core;

namespace ParquetNeo.Data;

/// <summary>Settings for <see cref="ParquetDataReader"/>.</summary>
public sealed record ParquetDataReaderOptions
{
    /// <summary>Codecs and safety limits of the file reader; the defaults when not set.</summary>
    public ParquetReaderOptions Reader { get; init; } = new();

    /// <summary>
    /// The names of the columns to read, in the order the reader gives them; every column that holds a single value per row
    /// when not set. Only those columns are read from the file.
    /// </summary>
    public IReadOnlyList<string>? Columns { get; init; }
}
