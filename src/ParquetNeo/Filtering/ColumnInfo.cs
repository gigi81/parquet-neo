using ParquetNeo.Core;
using ParquetNeo.Schema;
using ParquetNeo.Statistics;

namespace ParquetNeo.Filtering;

/// <summary>What a filter needs to know of one column of a row group: how its values compare, and what the statistics of the column chunk say.</summary>
/// <param name="Domain">How values of the column compare, or null when the column has no ordering the statistics can be trusted for.</param>
/// <param name="Statistics">What the writer recorded for the column chunk, or null when nothing usable.</param>
/// <param name="NumValues">The number of values, nulls included, in the column chunk.</param>
/// <param name="BloomFilterSource">Reads the bloom filter of the column chunk, or null when filters are not to be used.</param>
internal sealed record ColumnInfo(PrimitiveNode Column, ValueDomain? Domain, ColumnStatistics? Statistics, long NumValues, Func<BloomFilter?>? BloomFilterSource = null)
{
    private BloomFilter? _bloomFilter;
    private bool _bloomFilterRead;

    /// <summary>The bloom filter of the column chunk, read when first asked for, or null when there is none or it is not to be used.</summary>
    public BloomFilter? GetBloomFilter()
    {
        if (!_bloomFilterRead)
        {
            _bloomFilter = BloomFilterSource?.Invoke();
            _bloomFilterRead = true;
        }

        return _bloomFilter;
    }

    /// <summary>
    /// Whether the column has one entry per row, which is null or a value: not in a collection, and below no optional object. Only
    /// then do the null count and the number of values say how many rows are null.
    /// </summary>
    public bool IsSimple => Column.MaxRepetitionLevel == 0 && Column.MaxDefinitionLevel == (Column.Repetition == FieldRepetitionType.Optional ? 1 : 0);

    /// <summary>Whether it is known that no row is null.</summary>
    public bool HasNoNulls => IsSimple && (Column.Repetition == FieldRepetitionType.Required || Statistics?.NullCount == 0);

    /// <summary>Whether it is known that every row is null.</summary>
    public bool IsAllNull => IsSimple && Statistics?.NullCount is { } nulls && nulls == NumValues && NumValues > 0;
}
