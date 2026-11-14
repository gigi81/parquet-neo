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

    /// <summary>
    /// A condition that row groups are checked against by the statistics of their columns, to skip those that cannot hold a row that
    /// satisfies it. It only prunes: the rows of the row groups that are read are all given, whether they satisfy the condition or
    /// not. All the row groups are read when it is not set.
    /// </summary>
    public ParquetFilter? RowGroupFilter { get; init; }
}
