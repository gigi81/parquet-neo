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
        return OpenColumn(ColumnIndexOf(name));
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
    /// Opens a column like <see cref="OpenColumn(int)"/> after reading its whole column chunk into memory with asynchronous reads, so that
    /// reading the values does not wait for the stream. The chunk stays in memory until the reader is disposed, so this takes the
    /// compressed size of the column chunk of memory; use <see cref="OpenColumn(int)"/> for a chunk too big to hold.
    /// </summary>
    /// <exception cref="NotSupportedException">The column uses an unsupported encoding or lives in another file.</exception>
    public async ValueTask<ColumnReader> OpenColumnAsync(int index, CancellationToken cancellationToken = default)
    {
        var reader = OpenColumn(index);
        try
        {
            reader.Own(await _file.PrefetchAsync([(reader.ChunkStart, reader.ChunkEnd - reader.ChunkStart)], cancellationToken).ConfigureAwait(false));
            return reader;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>Opens a top-level column by name like <see cref="OpenColumnAsync(int, CancellationToken)"/>.</summary>
    public ValueTask<ColumnReader> OpenColumnAsync(string name, CancellationToken cancellationToken = default) =>
        OpenColumnAsync(ColumnIndexOf(name), cancellationToken);

    /// <summary>
    /// Opens a column to read only the rows of <paramref name="selection"/> like <see cref="OpenColumn(int, RowSelection)"/>, reading its
    /// page index and the pages that hold selected rows into memory with asynchronous reads: the pages that are skipped are not read.
    /// </summary>
    /// <exception cref="NotSupportedException">The file has no usable offset index for the column, or the selection has rows past the end of the group.</exception>
    public async ValueTask<ColumnReader> OpenColumnAsync(int index, RowSelection selection, CancellationToken cancellationToken = default)
    {
        _file.EnsureOpen();
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, ColumnCount);
        if (!_pages.ContainsKey(index))
        {
            using var _ = await _file.PrefetchAsync(_file.PageIndexRanges([Metadata.Columns[index]]), cancellationToken).ConfigureAwait(false);
            PagesOf(index);
        }

        var reader = OpenColumn(index, selection);
        try
        {
            var wanted = SelectedPageRanges(_pages[index]!, selection, reader.ChunkStart, reader.ChunkEnd);
            reader.Own(await _file.PrefetchAsync(wanted, cancellationToken).ConfigureAwait(false));
            return reader;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>The dictionary page and the pages of a column chunk that hold a row of the selection, with room for the page header that is read before each.</summary>
    private IEnumerable<(long Offset, long Length)> SelectedPageRanges(ColumnPages pages, RowSelection selection, long chunkStart, long chunkEnd)
    {
        var locations = pages.Locations;
        if (locations[0].Offset > chunkStart)
        {
            yield return (chunkStart, Math.Min(chunkEnd - chunkStart, Math.Max(locations[0].Offset - chunkStart, ColumnReader.HeaderPrefetch)));
        }

        int range = 0;
        for (int page = 0; page < locations.Count && range < selection.Ranges.Count; page++)
        {
            var (first, end) = pages.RowsOf(page, RowCount);
            while (range < selection.Ranges.Count && selection.Ranges[range].End <= first)
            {
                range++;
            }

            if (range < selection.Ranges.Count && selection.Ranges[range].Start < end)
            {
                long offset = locations[page].Offset;
                yield return (offset, Math.Min(chunkEnd - offset, Math.Max(locations[page].CompressedPageSize, ColumnReader.HeaderPrefetch)));
            }
        }
    }

    private int ColumnIndexOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return (_file.Schema.FindColumn([name]) ?? throw new ArgumentException($"There is no column named '{name}'.", nameof(name))).ColumnIndex;
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
        if (ranges is null)
        {
            return null;
        }

        var selection = new RowSelection(ranges);
        _file.Options.Metrics?.RecordRowsSkipped(RowCount - selection.RowCount);
        return selection;
    }

    /// <summary>
    /// Finds the rows that the filter may match like <see cref="SelectRows(ParquetFilter, IEnumerable{int})"/>, reading the page indexes
    /// and bloom filters it needs with asynchronous reads.
    /// </summary>
    /// <param name="columns">The positions in the schema of the columns that are going to be read.</param>
    public async ValueTask<RowSelection?> SelectRowsAsync(ParquetFilter filter, IEnumerable<int> columns, CancellationToken cancellationToken = default)
    {
        _file.EnsureOpen();
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(columns);
        var wanted = columns.ToList();
        foreach (int column in wanted)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(column);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(column, ColumnCount);
        }

        var ofFilter = _file.ColumnChunksOf(Metadata, filter.Columns).ToList();
        var chunks = wanted.Select(column => Metadata.Columns[column]).Concat(ofFilter).Distinct();
        var ranges = _file.PageIndexRanges(chunks).Concat(_file.BloomRanges(ofFilter));
        using var lease = await _file.PrefetchAsync(ranges, cancellationToken).ConfigureAwait(false);
        return SelectRows(filter, wanted);
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
