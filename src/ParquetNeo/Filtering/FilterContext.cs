using ParquetNeo.Core;
using ParquetNeo.Exceptions;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;
using ParquetNeo.Statistics;

namespace ParquetNeo.Filtering;

/// <summary>The row group a filter is evaluated for: finds its columns by path and keeps what it works out of them.</summary>
/// <param name="bloomFilters">Reads the bloom filter of a column chunk; null when filters are not to be read.</param>
internal sealed class FilterContext(ParquetSchema schema, IReadOnlyList<ColumnOrder>? columnOrders, RowGroup rowGroup, Func<ColumnChunk, BloomFilter?>? bloomFilters = null)
{
    private readonly Dictionary<string, ColumnInfo?> _columns = new(StringComparer.Ordinal);

    /// <summary>The column at a path from just below the root, or null when the file has none.</summary>
    public ColumnInfo? Find(IReadOnlyList<string> path)
    {
        string key = string.Join('\0', path);
        if (_columns.TryGetValue(key, out var known))
        {
            return known;
        }

        ColumnInfo? info = null;
        if (schema.FindColumn(path) is { } column && column.ColumnIndex < rowGroup.Columns.Count)
        {
            var chunk = rowGroup.Columns[column.ColumnIndex];
            info = new ColumnInfo(column, ValueDomain.For(column), Decode(column, chunk), chunk.MetaData?.NumValues ?? 0, bloomFilters is null ? null : () => bloomFilters(chunk));
        }

        _columns.Add(key, info);
        return info;
    }

    private ColumnStatistics? Decode(PrimitiveNode column, ColumnChunk chunk)
    {
        try
        {
            return ColumnStatisticsDecoder.Decode(column, chunk, columnOrders);
        }
        catch (MetadataException)
        {
            // Statistics that cannot be read prove nothing; the column reader reports the damage if the row group is read.
            return null;
        }
    }
}
