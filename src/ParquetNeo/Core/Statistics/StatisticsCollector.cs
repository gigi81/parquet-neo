using ParquetNeo.Metadata;

namespace ParquetNeo.Core.Statistics;

// A namespace called Statistics would otherwise hide the Statistics type of the metadata.
using Statistics = ParquetNeo.Metadata.Statistics;

/// <summary>
/// Gathers the minimum, maximum and counts of the values written to one column chunk, to be stored in the chunk's
/// <see cref="Statistics"/>. The ordering used is the one the column's logical and physical type define.
/// </summary>
internal abstract class StatisticsCollector
{
    /// <summary>Adds fixed-width values; ignored when this collector does not track values of type <typeparamref name="T"/>.</summary>
    public void Add<T>(ReadOnlySpan<T> values) where T : unmanaged
    {
        if (this is IStatisticsSink<T> sink)
        {
            sink.Add(values);
        }
    }

    /// <summary>Adds one BYTE_ARRAY or FIXED_LEN_BYTE_ARRAY value.</summary>
    public virtual void AddBinary(ReadOnlySpan<byte> value)
    {
    }

    /// <param name="nullCount">The number of null rows in the chunk.</param>
    public abstract Statistics Build(long nullCount);
}
