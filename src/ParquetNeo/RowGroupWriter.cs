using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using ParquetNeo.Core;
using ParquetNeo.Metadata;
using ParquetNeo.Metadata.Headers;

namespace ParquetNeo;

/// <summary>
/// Writes one row group: every column of the schema, in schema order, each with the same number of rows.
/// Create it with <see cref="ParquetFileWriter.AppendRowGroup"/>, call <see cref="NextColumn"/> once per column,
/// and dispose it (or call <see cref="Close"/>) to finish the group.
/// </summary>
public sealed class RowGroupWriter : IDisposable, IAsyncDisposable
{
    private readonly ParquetFileWriter _file;
    private readonly int _ordinal;
    private readonly List<ColumnChunk> _chunks = new();
    private readonly List<(ColumnMetaData Metadata, BloomFilter Filter)> _bloomFilters = [];
    private readonly List<(ColumnChunk Chunk, PageIndex Index)> _pageIndexes = [];
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
        ValidateColumns(columns, maxDegreeOfParallelism);
        int count = columns.Count;
        var encoded = new Encoded?[count];
        var spare = new ConcurrentBag<WriterBuffers>();
        try
        {
            try
            {
                Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism, CancellationToken = cancellationToken }, i =>
                    encoded[i] = Encode(i, columns[i], spare));
            }
            catch (AggregateException exception) when (exception.InnerExceptions.Count > 0)
            {
                ExceptionDispatchInfo.Capture(exception.InnerExceptions[0]).Throw();
            }

            for (int i = 0; i < count; i++)
            {
                long start = _file.Position;
                _file.Write(encoded[i]!.Sink.Written);
                RecordEncoded(encoded[i]!, start);
                encoded[i]!.Sink.Dispose();
                encoded[i] = null;
            }
        }
        finally
        {
            ReleaseEncoded(encoded, spare);
        }

        _nextColumn = count;
        _allColumnsWritten = true;
    }

    /// <summary>
    /// Writes every column of the group like <see cref="WriteColumns(IReadOnlyList{Action{ColumnWriter}}, CancellationToken)"/>, on as many
    /// threads as the machine has processors, and without blocking while the columns are written to the stream.
    /// </summary>
    public ValueTask WriteColumnsAsync(IReadOnlyList<Action<ColumnWriter>> columns, CancellationToken cancellationToken = default) =>
        WriteColumnsAsync(columns, Environment.ProcessorCount, cancellationToken);

    /// <summary>
    /// Writes every column of the group like <see cref="WriteColumns(IReadOnlyList{Action{ColumnWriter}}, int, CancellationToken)"/>, with
    /// the same rules and the same file, but the columns are encoded on the thread pool without holding the caller's thread, and are
    /// written to the stream with asynchronous writes, so a slow stream does not block a thread either.
    /// </summary>
    /// <remarks>
    /// The encoded columns of the group are held in memory until all of them are done, as with the synchronous method. Use
    /// <see cref="WriteColumnAsync"/> for a group that is too big to hold: it holds one column at a time.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="columns"/> is null.</exception>
    /// <exception cref="ArgumentException">An action is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxDegreeOfParallelism"/> is below 1.</exception>
    /// <exception cref="InvalidOperationException">A column has been started already, there is not one action per column, or the columns have different row counts.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled; the group is not complete and disposing it abandons the file.</exception>
    public async ValueTask WriteColumnsAsync(IReadOnlyList<Action<ColumnWriter>> columns, int maxDegreeOfParallelism, CancellationToken cancellationToken = default)
    {
        ValidateColumns(columns, maxDegreeOfParallelism);
        int count = columns.Count;
        var encoded = new Encoded?[count];
        var spare = new ConcurrentBag<WriterBuffers>();
        try
        {
            await Parallel.ForAsync(0, count, new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism, CancellationToken = cancellationToken }, (i, _) =>
            {
                encoded[i] = Encode(i, columns[i], spare);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

            for (int i = 0; i < count; i++)
            {
                long start = _file.Position;
                await _file.WriteAsync(encoded[i]!.Sink.WrittenMemory, cancellationToken).ConfigureAwait(false);
                RecordEncoded(encoded[i]!, start);
                encoded[i]!.Sink.Dispose();
                encoded[i] = null;
            }
        }
        finally
        {
            ReleaseEncoded(encoded, spare);
        }

        _nextColumn = count;
        _allColumnsWritten = true;
    }

    /// <summary>
    /// Writes the next column of the group: <paramref name="write"/> puts its values in the <see cref="ColumnWriter"/>, which encodes
    /// them into memory, and the encoded column is then written to the stream with an asynchronous write. So, unlike
    /// <see cref="NextColumn"/>, no write to the stream blocks the thread, and the group needs the memory of one encoded column.
    /// The column is complete when this returns, and the file is the same as <see cref="NextColumn"/> makes.
    /// </summary>
    /// <param name="write">Writes the values of the column. It must not keep the <see cref="ColumnWriter"/> after it returns. It runs on the calling thread.</param>
    /// <exception cref="InvalidOperationException">All columns have been written, the group is closed, or the column has another row count than the ones before.</exception>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    public async ValueTask WriteColumnAsync(Action<ColumnWriter> write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        EnsureOpen();
        CompleteCurrent();
        int ordinal = _nextColumn;
        if (ordinal >= _file.Schema.Columns.Count)
        {
            throw new InvalidOperationException($"All {_file.Schema.Columns.Count} columns of this row group have been written.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var spare = new ConcurrentBag<WriterBuffers>();
        Encoded? encoded = null;
        try
        {
            encoded = Encode(ordinal, write, spare);
            long start = _file.Position;
            await _file.WriteAsync(encoded.Sink.WrittenMemory, cancellationToken).ConfigureAwait(false);
            RecordEncoded(encoded, start);
            _nextColumn = ordinal + 1;
            if (_nextColumn == _file.Schema.Columns.Count)
            {
                _allColumnsWritten = true;
            }
        }
        finally
        {
            encoded?.Sink.Dispose();
            foreach (var buffers in spare)
            {
                buffers.Dispose();
            }
        }
    }

    private void ValidateColumns(IReadOnlyList<Action<ColumnWriter>> columns, int maxDegreeOfParallelism)
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
    }

    /// <summary>Encodes one column into a buffer of its own, reusing the buffers of a column that is done when there are any. Can run on any thread.</summary>
    private Encoded Encode(int ordinal, Action<ColumnWriter> write, ConcurrentBag<WriterBuffers> spare)
    {
        if (!spare.TryTake(out var buffers))
        {
            buffers = new WriterBuffers();
        }

        var sink = new DetachedColumnSink();
        try
        {
            var writer = _file.CreateDetachedColumnWriter(_file.Schema.Columns[ordinal], sink, buffers);
            write(writer);
            var chunk = writer.Complete();
            return new Encoded(sink, writer, chunk);
        }
        catch
        {
            sink.Dispose();
            throw;
        }
        finally
        {
            spare.Add(buffers);
        }
    }

    /// <summary>Adds an encoded column, now written to the file from <paramref name="start"/>, to the group, moving its offsets from the start of the chunk to the file.</summary>
    private void RecordEncoded(Encoded encoded, long start)
    {
        var (_, writer, chunk) = encoded;
        var metadata = chunk.MetaData!;
        metadata.DataPageOffset += start;
        if (metadata.DictionaryPageOffset is { } dictionaryOffset)
        {
            metadata.DictionaryPageOffset = dictionaryOffset + start;
        }

        writer.PageIndex?.Shift(start);
        Record(writer, chunk, writer.FirstPageOffset + start);
    }

    /// <summary>Whatever happened, the storage of the buffers and of the columns that are not written goes back to the pool.</summary>
    private static void ReleaseEncoded(Encoded?[] encoded, ConcurrentBag<WriterBuffers> spare)
    {
        foreach (var buffers in spare)
        {
            buffers.Dispose();
        }

        foreach (var column in encoded)
        {
            column?.Sink.Dispose();
        }
    }

    private sealed record Encoded(DetachedColumnSink Sink, ColumnWriter Writer, ColumnChunk Chunk);

    /// <summary>Finishes the group and records it in the file.</summary>
    /// <exception cref="InvalidOperationException">Columns are missing, or the columns have different row counts.</exception>
    public void Close()
    {
        if (!BeginClose())
        {
            return;
        }

        WriteBloomFilters();
        EndClose();
    }

    /// <summary>Finishes the group like <see cref="Close"/>, writing its bloom filters to the stream without blocking.</summary>
    /// <exception cref="InvalidOperationException">Columns are missing, or the columns have different row counts.</exception>
    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (!BeginClose())
        {
            return;
        }

        await WriteBloomFiltersAsync(cancellationToken).ConfigureAwait(false);
        EndClose();
    }

    /// <summary>Checks that the group can be closed; returns false when it is closed already, and marks it closed otherwise.</summary>
    private bool BeginClose()
    {
        if (_closed)
        {
            return false;
        }

        EnsureOpen();
        CompleteCurrent();
        int columnCount = _file.Schema.Columns.Count;
        if (_nextColumn != columnCount)
        {
            throw new InvalidOperationException($"Only {_nextColumn} of {columnCount} columns were written to this row group.");
        }

        _closed = true;
        return true;
    }

    private void EndClose()
    {
        _file.AddPageIndexes(_pageIndexes);
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
        if (CanFinishOnDispose())
        {
            try
            {
                Close();
                return;
            }
            catch (InvalidOperationException)
            {
                // fall through: the group is incomplete or inconsistent
            }

            _file.Abandon();
        }
        else if (!_closed && _file.IsUsable)
        {
            _file.Abandon();
        }
    }

    /// <summary>Finishes the group like <see cref="Dispose"/>, with <see cref="CloseAsync"/>, so its bloom filters are written without blocking.</summary>
    public async ValueTask DisposeAsync()
    {
        if (CanFinishOnDispose())
        {
            try
            {
                await CloseAsync().ConfigureAwait(false);
                return;
            }
            catch (InvalidOperationException)
            {
                // fall through: the group is incomplete or inconsistent
            }

            _file.Abandon();
        }
        else if (!_closed && _file.IsUsable)
        {
            _file.Abandon();
        }
    }

    private bool CanFinishOnDispose() =>
        !_closed && _file.IsUsable && (_current is not null || _allColumnsWritten) && _nextColumn == _file.Schema.Columns.Count;

    /// <summary>Writes the bloom filters of the columns after the columns of the group, each as its header and its bits, and records where.</summary>
    private void WriteBloomFilters()
    {
        foreach (var (metadata, filter) in _bloomFilters)
        {
            var bytes = SerializeBloomFilter(filter);
            metadata.BloomFilterOffset = _file.Position;
            metadata.BloomFilterLength = bytes.Length;
            _file.Write(bytes);
        }
    }

    private async ValueTask WriteBloomFiltersAsync(CancellationToken cancellationToken)
    {
        foreach (var (metadata, filter) in _bloomFilters)
        {
            var bytes = SerializeBloomFilter(filter);
            metadata.BloomFilterOffset = _file.Position;
            metadata.BloomFilterLength = bytes.Length;
            await _file.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
    }

    private static byte[] SerializeBloomFilter(BloomFilter filter)
    {
        var header = new ArrayBufferWriter<byte>();
        new BloomFilterHeader { NumBytes = filter.ByteCount }.Serialize(header);
        var bytes = new byte[header.WrittenCount + filter.ByteCount];
        header.WrittenSpan.CopyTo(bytes);
        filter.CopyTo(bytes.AsSpan(header.WrittenCount));
        return bytes;
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
        if (column.BloomFilter is { } filter)
        {
            _bloomFilters.Add((chunk.MetaData!, filter));
        }

        if (column.PageIndex is { } pageIndex)
        {
            _pageIndexes.Add((chunk, pageIndex));
        }

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
