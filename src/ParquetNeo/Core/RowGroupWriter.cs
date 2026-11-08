using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
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
    private bool _allColumnsWritten;

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

    /// <summary>
    /// Writes every column of the group, encoding up to <paramref name="maxDegreeOfParallelism"/> of them at a time. The columns
    /// are encoded into buffers of their own and then copied to the file in the order of the schema, so the file is the same
    /// as the one <see cref="NextColumn"/> makes. The group must not have started: this does all of its columns.
    /// </summary>
    /// <param name="columns">What to write to each column, in the order of the schema. They run on different threads.</param>
    /// <exception cref="InvalidOperationException">A column has been started already, or there is not one action per column.</exception>
    internal void WriteColumns(IReadOnlyList<Action<ColumnWriter>> columns, int maxDegreeOfParallelism)
    {
        EnsureOpen();
        CompleteCurrent();
        var schemaColumns = _file.Schema.Columns;
        if (_nextColumn != 0 || columns.Count != schemaColumns.Count)
        {
            throw new InvalidOperationException($"All {schemaColumns.Count} columns must be written at once, into a row group that has none yet.");
        }

        int count = columns.Count;
        var sinks = new DetachedColumnSink?[count];
        var writers = new ColumnWriter?[count];
        var chunks = new ColumnChunk?[count];
        var spare = new ConcurrentBag<WriterBuffers>();
        try
        {
            try
            {
                Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, maxDegreeOfParallelism) }, i =>
                {
                    if (!spare.TryTake(out var buffers))
                    {
                        buffers = new WriterBuffers();
                    }

                    var sink = new DetachedColumnSink();
                    sinks[i] = sink;
                    var writer = _file.CreateDetachedColumnWriter(schemaColumns[i], sink, buffers);
                    columns[i](writer);
                    chunks[i] = writer.Complete();
                    writers[i] = writer;
                    spare.Add(buffers);
                });
            }
            catch (AggregateException exception) when (exception.InnerExceptions.Count > 0)
            {
                ExceptionDispatchInfo.Capture(exception.InnerExceptions[0]).Throw();
            }

            for (int i = 0; i < count; i++)
            {
                long start = _file.Position;
                _file.Write(sinks[i]!.Written);
                var chunk = chunks[i]!;
                var metadata = chunk.MetaData!;
                metadata.DataPageOffset += start;
                if (metadata.DictionaryPageOffset is { } dictionaryOffset)
                {
                    metadata.DictionaryPageOffset = dictionaryOffset + start;
                }

                Record(writers[i]!, chunk, writers[i]!.FirstPageOffset + start);
                sinks[i]!.Dispose();
                sinks[i] = null;
            }
        }
        finally
        {
            // Whatever happened, the storage of the buffers goes back to the pool.
            foreach (var buffers in spare)
            {
                buffers.Dispose();
            }

            foreach (var sink in sinks)
            {
                sink?.Dispose();
            }
        }

        _nextColumn = count;
        _allColumnsWritten = true;
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
            if ((_current is not null || _allColumnsWritten) && _nextColumn == _file.Schema.Columns.Count)
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
        Record(column, column.Complete(), column.FirstPageOffset);
    }

    /// <summary>Adds the finished chunk of a column to the group, after checking that it has as many rows as the columns before it.</summary>
    private void Record(ColumnWriter column, ColumnChunk chunk, long firstPageOffset)
    {
        if (_rows < 0)
        {
            _rows = column.RowCount;
            _firstPageOffset = firstPageOffset;
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
