using ParquetNeo.Metadata;

namespace ParquetNeo.Core;

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
    /// <exception cref="NotSupportedException">The column is nested or repeated, uses an unsupported encoding or lives in another file.</exception>
    public ColumnReader OpenColumn(int index)
    {
        _file.EnsureOpen();
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, ColumnCount);
        return new ColumnReader(_file, _file.Schema.Columns[index], Metadata.Columns[index]);
    }

    /// <summary>Opens a top-level column by name.</summary>
    public ColumnReader OpenColumn(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var column = _file.Schema.FindColumn([name]) ?? throw new ArgumentException($"There is no column named '{name}'.", nameof(name));
        return OpenColumn(column.ColumnIndex);
    }
}
