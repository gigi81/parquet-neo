using ParquetNeo.Core;

namespace ParquetNeo.Serialization;

/// <summary>Settings for <see cref="ParquetSerializer"/>.</summary>
public sealed record ParquetSerializerOptions
{
    /// <summary>Compression, page size, encodings and the footer; the defaults when not set.</summary>
    public ParquetWriterOptions Writer { get; init; } = new();

    /// <summary>
    /// The number of rows in a row group. A group is encoded and written when it is full, so this is also how many rows the
    /// serializer holds in memory (as column values, not as objects) at a time. Larger groups compress better and give
    /// readers more to skip at once; smaller ones need less memory. Defaults to 100,000.
    /// </summary>
    public int RowGroupSize { get; init; } = 100_000;

    /// <summary>
    /// How many columns of a row group are encoded at the same time. The columns are independent, so encoding them on several
    /// threads makes a wide or compressed table faster, and the file is the same either way. Defaults to 1, which encodes on the
    /// calling thread; <see cref="Environment.ProcessorCount"/> is a good value for a throughput-bound job.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = 1;
}
