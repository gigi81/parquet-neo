using ParquetNeo.Exceptions;
using ParquetNeo.Metadata;
using ParquetNeo.Statistics;

namespace ParquetNeo;

/// <summary>Gives access to the columns of one row group.</summary>
public sealed class RowGroupReader
{
    private readonly ParquetFileReader _file;

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
