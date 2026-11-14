using ParquetNeo.Metadata;
using ParquetNeo.Schema;
using ParquetNeo.Statistics;

namespace ParquetNeo.Core;

/// <summary>The page index of one column chunk as read from a file: where the pages are and, when the file has them, their bounds.</summary>
internal sealed class ColumnPages(List<PageLocation> locations, ColumnIndex? bounds)
{
    public List<PageLocation> Locations { get; } = locations;

    /// <summary>The bounds of the pages; null when the file has none for this chunk.</summary>
    public ColumnIndex? Bounds { get; } = bounds;

    /// <summary>The first row of a page and the one after its last, in a row group of <paramref name="rowCount"/> rows.</summary>
    public (long First, long End) RowsOf(int page, long rowCount) =>
        (Locations[page].FirstRowIndex, page + 1 < Locations.Count ? Locations[page + 1].FirstRowIndex : rowCount);

    /// <summary>What the column index says of a page, as statistics, or null when the page index has no bounds that can be trusted for it.</summary>
    public ColumnStatistics? StatisticsOf(int page, PrimitiveNode column, IReadOnlyList<ColumnOrder>? columnOrders)
    {
        var index = Bounds;
        if (index is null)
        {
            return null;
        }

        long? nulls = index.NullCounts is { } counts ? counts[page] : null;
        if (index.NullPages[page])
        {
            // Only nulls: there is no bound, and the count says so, which the NullCounts of the index give when they are there.
            return new ColumnStatistics(null, null, nulls, null, null, null);
        }

        var raw = new Metadata.Statistics { MinValue = index.MinValues[page], MaxValue = index.MaxValues[page], NullCount = nulls };
        return ColumnStatisticsDecoder.Decode(column, raw, columnOrders);
    }
}
