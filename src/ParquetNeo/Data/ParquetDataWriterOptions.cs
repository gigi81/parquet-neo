using ParquetNeo.ColumnTypes;
using ParquetNeo.Core;

namespace ParquetNeo.Data;

/// <summary>Settings for <see cref="ParquetDataWriter"/>.</summary>
public sealed record ParquetDataWriterOptions
{
    /// <summary>Compression, page size, encodings and the footer; the defaults when not set.</summary>
    public ParquetWriterOptions Writer { get; init; } = new();

    /// <summary>
    /// The number of rows in a row group. A group is encoded and written when it is full, so this is also how many rows are held
    /// in memory (as column values) at a time. Defaults to 100,000.
    /// </summary>
    public int RowGroupSize { get; init; } = 100_000;

    /// <summary>
    /// How many columns of a row group are encoded at the same time. The columns are independent, so encoding them on several
    /// threads makes a wide or compressed table faster, and the file is the same either way. Defaults to 1, which encodes on the
    /// calling thread; <see cref="Environment.ProcessorCount"/> is a good value for a throughput-bound job.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = 1;

    /// <summary>
    /// Adjusts the column written for a column of the reader, by its name: the precision and scale of a decimal, the unit of a
    /// time or timestamp. They override what the reader says about the column itself.
    /// </summary>
    public IReadOnlyDictionary<string, ColumnSettings>? ColumnSettings { get; init; }
}
