namespace ParquetNeo;

/// <summary>
/// Some rows of a row group, as ascending runs that do not touch. Given to <see cref="RowGroupReader.OpenColumn(int, RowSelection)"/>
/// so that the column is read for only those rows; the pages that hold none of them are not read. Rows count from the first of the
/// group, and a row of a repeated column is a whole record (a list), however many values it has.
/// </summary>
public sealed class RowSelection
{
    /// <exception cref="ArgumentException">A run is empty or negative, or the runs are not ascending and apart from each other.</exception>
    public RowSelection(IEnumerable<RowRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        var list = new List<RowRange>();
        long previousEnd = -1;
        long total = 0;
        foreach (var range in ranges)
        {
            if (range.Start < 0 || range.Count <= 0)
            {
                throw new ArgumentException($"The run starting at {range.Start} of {range.Count} rows is not a run of rows.", nameof(ranges));
            }

            if (range.Start <= previousEnd)
            {
                throw new ArgumentException("The runs of rows must be in ascending order, with a row between one and the next.", nameof(ranges));
            }

            list.Add(range);
            previousEnd = range.End;
            total += range.Count;
        }

        Ranges = list;
        RowCount = total;
    }

    public IReadOnlyList<RowRange> Ranges { get; }

    /// <summary>The number of rows in the selection.</summary>
    public long RowCount { get; }
}
