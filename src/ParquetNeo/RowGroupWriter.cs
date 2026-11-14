using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using ParquetNeo.Core;
using ParquetNeo.Metadata;

namespace ParquetNeo;

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
    /// Writes every column of the group on as many threads as the machine has processors; see
    /// <see cref="WriteColumns(IReadOnlyList{Action{ColumnWriter}}, int, CancellationToken)"/>.
    /// </summary>
    public void WriteColumns(IReadOnlyList<Action<ColumnWriter>> columns, CancellationToken cancellationToken = default) =>
        WriteColumns(columns, Environment.ProcessorCount, cancellationToken);

    /// <summary>
    /// Writes every column of the group, encoding up to <paramref name="maxDegreeOfParallelism"/> of them at a time. Encoding
    /// (the dictionary, the levels, compression, statistics) is most of the time of writing, so a group of several columns takes
    /// about as long as its slowest column instead of the sum of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The columns are encoded into buffers of their own, and then copied to the file in the order of the schema, so the file is
    /// the same, byte for byte, as the one <see cref="NextColumn"/> makes whatever the degree of parallelism. The group must not
    /// have started: this does all of its columns.
    /// </para>
    /// <para>
    /// The actions run on different threads, one thread for each action, so they must not share anything that is not safe to share
    /// (the arrays they read from are fine as long as nothing writes to them). The <see cref="ColumnWriter"/> an action gets is for
    /// that action alone and only until it returns: write the values of the column to it and do not keep it. The action does not
    /// complete the column; the group does.
    /// </para>
    /// <para>
    /// The encoded columns of the group are held in memory until all of them are done, so this takes about the size of the group
    /// (compressed) more memory than <see cref="NextColumn"/>, which writes each column to the file as it goes. If an action throws,
    /// that exception is thrown from here, nothing of the group is written, and disposing the group abandons the file. Use
    /// <see cref="NextColumn"/> for a group that is too big to hold, or for a single column.
    /// </para>
    /// </remarks>
    /// <param name="columns">What to write to each column, in the order of the schema.</param>
    /// <param name="maxDegreeOfParallelism">The most columns encoded at the same time; at least 1.</param>
    /// <param name="cancellationToken">Stops the columns that have not started; the exception is an <see cref="OperationCanceledException"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="columns"/> is null.</exception>
    /// <exception cref="ArgumentException">An action is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxDegreeOfParallelism"/> is below 1.</exception>
    /// <exception cref="InvalidOperationException">A column has been started already, there is not one action per column, or the columns have different row counts.</exception>
    public void WriteColumns(IReadOnlyList<Action<ColumnWriter>> columns, int maxDegreeOfParallelism, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDegreeOfParallelism, 1);
        if (columns.Any(action => action is null))
        {
            throw new ArgumentException("An action to write a column is null.", nameof(columns));
        }

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
                Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism, CancellationToken = cancellationToken }, i =>
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
