using ParquetNeo.Core;
using ParquetNeo.Exceptions;
using ParquetNeo.Filtering;
using ParquetNeo.Metadata;
using ParquetNeo.Statistics;

namespace ParquetNeo;

/// <summary>Gives access to the columns of one row group.</summary>
public sealed class RowGroupReader
{
    private readonly ParquetFileReader _file;
    private readonly Dictionary<int, ColumnPages?> _pages = new();

    internal RowGroupReader(ParquetFileReader file, int index, RowGroup metadata)
    {
        _file = file;
        Index = index;
        Metadata = metadata;
    }

    public int Index { get; }

    public RowGroup Metadata { get; }

    public long RowCount => Metadata.NumRows;

    public int ColumnCount => _file.Schema.Columns.Count;

    /// <summary>Opens a column by its position in the schema (depth-first order).</summary>
    /// <exception cref="NotSupportedException">The column uses an unsupported encoding or lives in another file.</exception>
    public ColumnReader OpenColumn(int index)
    {
        _file.EnsureOpen();
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, ColumnCount);
        return new ColumnReader(_file, _file.Schema.Columns[index], Metadata.Columns[index], Metadata.NumRows);
    }

    /// <summary>Opens a top-level column by name.</summary>
    public ColumnReader OpenColumn(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var column = _file.Schema.FindColumn([name]) ?? throw new ArgumentException($"There is no column named '{name}'.", nameof(name));
        return OpenColumn(column.ColumnIndex);
    }

    /// <summary>
    /// Opens a column to read only the rows of <paramref name="selection"/>: the pages that hold none of them are not read, and what
    /// is read is those rows one after the other, as if the others were not there. Needs the offset index of the column.
    /// </summary>
    /// <exception cref="NotSupportedException">The file has no usable offset index for the column, or the selection has rows past the end of the group.</exception>
    public ColumnReader OpenColumn(int index, RowSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var reader = OpenColumn(index);
        try
        {
            if (selection.Ranges.Count > 0 && selection.Ranges[^1].End > RowCount)
            {
                throw new NotSupportedException($"The selection has rows past the {RowCount} rows of the row group.");
            }

            var pages = PagesOf(index) ?? throw new NotSupportedException($"Column '{reader.Column.Name}' has no usable offset index, so its pages cannot be skipped.");
            reader.Select(selection, pages.Locations);
            return reader;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The rows of this group that the filter may match by the page index of its columns, to be given to <see cref="OpenColumn(int, RowSelection)"/>
    /// for each of <paramref name="columns"/>; <c>null</c> when there is nothing to gain: the filter cannot rule out a page, or one of
    /// the columns has no offset index. The rows of the pages kept that do not satisfy the filter are in it, so check them as well when
    /// the result must hold exactly.
    /// </summary>
    /// <param name="columns">The positions in the schema of the columns that are going to be read.</param>
    public RowSelection? SelectRows(ParquetFilter filter, IEnumerable<int> columns)
    {
        _file.EnsureOpen();
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(columns);
        foreach (int column in columns)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(column);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(column, ColumnCount);
            if (PagesOf(column) is null)
            {
                return null;
            }
        }

        var ranges = PageSelector.Select(filter, _file.Schema, _file.Metadata.ColumnOrders, Metadata, _file.ReadBloomFilter, PagesOf);
        return ranges is null ? null : new RowSelection(ranges);
    }

    private ColumnPages? PagesOf(int column)
    {
        if (!_pages.TryGetValue(column, out var pages))
        {
            _pages[column] = pages = _file.ReadColumnPages(Metadata.Columns[column], RowCount);
        }

        return pages;
    }

    /// <summary>
    /// Returns the statistics recorded for a column in this row group, or <c>null</c> when the writer recorded none.
    /// Works for every column.
    /// </summary>
    /// <exception cref="MetadataException">The recorded minimum or maximum has the wrong size for the column's type.</exception>
    public ColumnStatistics? GetStatistics(int index)
    {
        _file.EnsureOpen();
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, ColumnCount);
        return ColumnStatisticsDecoder.Decode(_file.Schema.Columns[index], Metadata.Columns[index], _file.Metadata.ColumnOrders);
    }

    /// <summary>Returns the statistics of a top-level column by name; see <see cref="GetStatistics(int)"/>.</summary>
    public ColumnStatistics? GetStatistics(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var column = _file.Schema.FindColumn([name]) ?? throw new ArgumentException($"There is no column named '{name}'.", nameof(name));
        return GetStatistics(column.ColumnIndex);
    }

    /// <summary>
    /// Whether the row group can hold a row that satisfies the filter: false when the statistics of its columns show that none does.
    /// See <see cref="ParquetFilter"/> for what that means for the rows.
    /// </summary>
    /// <exception cref="ArgumentException">The filter compares a column with a value of a type that does not suit the column.</exception>
    public bool MayMatch(ParquetFilter filter)
    {
        _file.EnsureOpen();
        ArgumentNullException.ThrowIfNull(filter);
        return filter.MayMatch(_file.Schema, _file.Metadata.ColumnOrders, Metadata, _file.ReadBloomFilter);
    }
}
