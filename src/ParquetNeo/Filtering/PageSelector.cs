using ParquetNeo.Core;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;
using ParquetNeo.Statistics;

namespace ParquetNeo.Filtering;

/// <summary>
/// Works out which rows of a row group a filter can match from the page index: the row group is cut where a page of one of the columns
/// the filter uses starts, and the filter is evaluated for each stretch with the bounds of the pages it lies in, as it is for the row group
/// with the bounds of the whole column. A stretch the filter rules out is left out.
/// </summary>
internal static class PageSelector
{
    /// <summary>
    /// The runs of rows that may match, or null when that is all of them or the page index says nothing of the columns of the filter.
    /// </summary>
    /// <param name="pagesOf">The page index of a column of the row group, by its position in the schema; null when it has none.</param>
    public static List<RowRange>? Select(
        ParquetFilter filter,
        ParquetSchema schema,
        IReadOnlyList<ColumnOrder>? columnOrders,
        RowGroup rowGroup,
        Func<ColumnChunk, BloomFilter?>? bloomFilters,
        Func<int, ColumnPages?> pagesOf)
    {
        long rowCount = rowGroup.NumRows;
        var paths = new List<IReadOnlyList<string>>();
        filter.CollectColumns(paths);
        var indexed = new List<(PrimitiveNode Column, ColumnPages Pages)>();
        foreach (var path in paths)
        {
            if (schema.FindColumn(path) is { } column
                && column.ColumnIndex < rowGroup.Columns.Count
                && indexed.TrueForAll(i => i.Column.ColumnIndex != column.ColumnIndex)
                && pagesOf(column.ColumnIndex) is { Bounds: not null } pages)
            {
                indexed.Add((column, pages));
            }
        }

        if (indexed.Count == 0 || rowCount <= 0)
        {
            return null;
        }

        // The stretches: from one start of a page of any of the columns to the next.
        var starts = new SortedSet<long> { 0 };
        foreach (var (_, pages) in indexed)
        {
            foreach (var location in pages.Locations)
            {
                starts.Add(location.FirstRowIndex);
            }
        }

        // A bloom filter is read once for the row group, not once for every stretch.
        var filters = new Dictionary<ColumnChunk, BloomFilter?>();
        Func<ColumnChunk, BloomFilter?>? memo = bloomFilters is null ? null : chunk =>
        {
            if (!filters.TryGetValue(chunk, out var read))
            {
                filters[chunk] = read = bloomFilters(chunk);
            }

            return read;
        };

        var cursors = new int[indexed.Count];
        var selected = new List<RowRange>();
        long selectedRows = 0;
        var bounds = starts.ToArray();
        for (int i = 0; i < bounds.Length; i++)
        {
            long from = bounds[i];
            long to = i + 1 < bounds.Length ? bounds[i + 1] : rowCount;
            var overrides = new Dictionary<int, (ColumnStatistics Statistics, long NumValues)>();
            for (int c = 0; c < indexed.Count; c++)
            {
                var (column, pages) = indexed[c];
                while (cursors[c] + 1 < pages.Locations.Count && pages.Locations[cursors[c] + 1].FirstRowIndex <= from)
                {
                    cursors[c]++;
                }

                var (pageFirst, pageEnd) = pages.RowsOf(cursors[c], rowCount);
                ColumnStatistics? statistics = null;
                try
                {
                    statistics = pages.StatisticsOf(cursors[c], column, columnOrders);
                }
                catch (Exceptions.MetadataException)
                {
                    // Bounds that cannot be read prove nothing about the page.
                }

                if (statistics is not null)
                {
                    overrides[column.ColumnIndex] = (statistics, pageEnd - pageFirst);
                }
            }

            var context = new FilterContext(schema, columnOrders, rowGroup, memo, overrides);
            if (filter.Evaluate(context) == FilterOutcome.None)
            {
                continue;
            }

            if (selected.Count > 0 && selected[^1].End == from)
            {
                selected[^1] = new RowRange(selected[^1].Start, selected[^1].Count + (to - from));
            }
            else
            {
                selected.Add(new RowRange(from, to - from));
            }

            selectedRows += to - from;
        }

        return selectedRows == rowCount ? null : selected;
    }
}
