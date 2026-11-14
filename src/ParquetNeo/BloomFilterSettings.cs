namespace ParquetNeo;

/// <summary>A bloom filter for a column, see <see cref="ParquetWriterOptions.ColumnBloomFilters"/>.</summary>
public sealed record BloomFilterSettings
{
    /// <summary>
    /// The chance that the filter says a value may be in a row group when it is not, for as many different values as the row group
    /// has in the column; a smaller one takes more room. Between 0 and 1, exclusive. Defaults to 0.05.
    /// </summary>
    public double FalsePositiveRate { get; init; } = 0.05;

    /// <summary>The most bytes a filter takes; a column with so many different values that the filter would take more gets a filter of this size, which is less exact. Defaults to 8 MiB.</summary>
    public int MaxBytes { get; init; } = 8 * 1024 * 1024;
}
