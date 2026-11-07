using ParquetNeo.Metadata;

namespace ParquetNeo.Core;

/// <summary>
/// Writes one row group: every column of the schema, in schema order, each with the same number of rows.
/// Create it with <see cref="ParquetFileWriter.AppendRowGroup"/>, call <see cref="NextColumn"/> once per column,
/// and dispose it (or call <see cref="Close"/>) to finish the group.
/// </summary>
public sealed class RowGroupWriter : IDisposable
{
    private readonly ParquetFileWriter _file;
    private readonly int _ordinal;
    private readonly List<ColumnChunk> _chunks = new();
    private ColumnWriter? _current;
    private int _nextColumn;
    private long _rows = -1;
    private long _firstPageOffset = -1;
    private long _totalUncompressed;
    private long _totalCompressed;
    private bool _closed;

    internal RowGroupWriter(ParquetFileWriter file, int ordinal)
    {
        _file = file;
        _ordinal = ordinal;
    }

    /// <summary>The number of columns that have been started so far.</summary>
    public int ColumnsStarted => _nextColumn;

    /// <summary>Finishes the current column, if any, and returns a writer for the next one.</summary>
    /// <exception cref="InvalidOperationException">All columns have been written, or the group is closed.</exception>
    public ColumnWriter NextColumn()
    {
        EnsureOpen();
        CompleteCurrent();
        var columns = _file.Schema.Columns;
        if (_nextColumn >= columns.Count)
        {
            throw new InvalidOperationException($"All {columns.Count} columns of this row group have been written.");
        }

        return _current = _file.CreateColumnWriter(columns[_nextColumn++]);
    }

    /// <summary>Finishes the group and records it in the file.</summary>
    /// <exception cref="InvalidOperationException">Columns are missing, or the columns have different row counts.</exception>
    public void Close()
    {
        if (_closed)
        {
            return;
        }

        EnsureOpen();
        CompleteCurrent();
        int columnCount = _file.Schema.Columns.Count;
        if (_nextColumn != columnCount)
        {
            throw new InvalidOperationException($"Only {_nextColumn} of {columnCount} columns were written to this row group.");
        }

        _closed = true;
        _file.OnRowGroupClosed(new RowGroup
        {
            Columns = _chunks,
            TotalByteSize = _totalUncompressed,
            NumRows = _rows,
            FileOffset = _firstPageOffset,
            TotalCompressedSize = _totalCompressed,
            Ordinal = _ordinal <= short.MaxValue ? (short)_ordinal : null,
        });
    }

    /// <summary>
    /// Finishes the group if every column was written. Problems (a missing column, a column without values,
    /// mismatched row counts) are not reported from here, so that disposing never hides an exception that is
    /// already on its way: the file is abandoned instead and <see cref="ParquetFileWriter.Close"/> then fails.
    /// Call <see cref="Close"/> to get the error at the point where it happens.
    /// </summary>
    public void Dispose()
    {
        if (_closed || !_file.IsUsable)
        {
            return;
        }

        try
        {
            if (_current is not null && _nextColumn == _file.Schema.Columns.Count)
            {
                Close();
                return;
            }
        }
        catch (InvalidOperationException)
        {
            // fall through: the group is incomplete or inconsistent
        }

        _file.Abandon();
    }

    private void CompleteCurrent()
    {
        if (_current is null)
        {
            return;
        }

        var column = _current;
        _current = null;
        var chunk = column.Complete();
        if (_rows < 0)
        {
            _rows = column.RowCount;
            _firstPageOffset = column.FirstPageOffset;
        }
        else if (column.RowCount != _rows)
        {
            throw new InvalidOperationException(
                $"Column '{column.Column.Name}' has {column.RowCount} rows but the previous columns of this row group have {_rows}.");
        }

        _chunks.Add(chunk);
        _totalUncompressed += column.TotalUncompressedSize;
        _totalCompressed += column.TotalCompressedSize;
    }

    private void EnsureOpen()
    {
        if (_closed)
        {
            throw new InvalidOperationException("This row group is already closed.");
        }

        _file.EnsureUsable();
    }
}
