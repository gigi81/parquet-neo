using System.Buffers.Binary;
using System.Buffers;
using ParquetNeo.Compression;
using ParquetNeo.Core;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo;

/// <summary>
/// Writes a Parquet file, one row group at a time. Columns are written one leaf column at a time, with the definition and
/// repetition levels for nested ones. The stream only needs to be
/// writable: offsets are counted rather than read from the stream, so it need not be seekable.
/// </summary>
/// <example>
/// <code>
/// using var writer = new ParquetFileWriter(stream, schema);
/// using (var group = writer.AppendRowGroup())
/// {
///     group.NextColumn().WriteBatch(ids);
///     group.NextColumn().WriteStrings(names, definitionLevels);
/// }
/// </code>
/// The footer is written when the writer is closed or disposed. Nothing here is thread safe.
/// </example>
public sealed class ParquetFileWriter : IColumnSink, IDisposable, IAsyncDisposable
{
    private static readonly byte[] Magic = "PAR1"u8.ToArray();

    private readonly Stream _output;
    private readonly bool _leaveOpen;
    private readonly ParquetWriterOptions _options;
    private readonly ICodec _codec;
    private readonly List<RowGroup> _rowGroups = new();
    private readonly List<(ColumnChunk Chunk, PageIndex Index)> _pageIndexes = new();
    private RowGroupWriter? _openGroup;
    private long _totalRows;
    private bool _closed;
    private bool _abandoned;

    /// <param name="output">A writable stream, positioned where the file should start.</param>
    /// <param name="schema">The schema. Its leaf columns are written in depth-first order.</param>
    /// <param name="options">Compression, page size and footer settings; <c>null</c> for the defaults.</param>
    /// <param name="leaveOpen">Whether to keep <paramref name="output"/> open when the writer is closed.</param>
    /// <exception cref="NotSupportedException">The schema has an INT96 column, or the compression codec is not registered.</exception>
    public ParquetFileWriter(Stream output, ParquetSchema schema, ParquetWriterOptions? options = null, bool leaveOpen = false)
        : this(output, schema, options, leaveOpen, writeMagic: true)
    {
    }

    private ParquetFileWriter(Stream output, ParquetSchema schema, ParquetWriterOptions? options, bool leaveOpen, bool writeMagic)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(schema);
        if (!output.CanWrite)
        {
            throw new ArgumentException("The stream must be writable.", nameof(output));
        }

        _options = options ?? new ParquetWriterOptions();
        if (_options.DataPageSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), _options.DataPageSize, "The data page size must be at least 1.");
        }

        if (!Enum.IsDefined(_options.DataPageVersion))
        {
            throw new ArgumentOutOfRangeException(nameof(options), _options.DataPageVersion, "The data page version must be V1 or V2.");
        }
        ValidateSchema(schema);
        ValueEncodingRules.Validate(schema, _options.ColumnEncodings);
        DictionarySettingsRules.Validate(schema, _options.ColumnDictionaries);
        BloomFilterBuilder.Validate(schema, _options.ColumnBloomFilters);

        _output = output;
        _leaveOpen = leaveOpen;
        Schema = schema;
        _codec = _options.Codecs.Get(_options.Compression);

        if (writeMagic)
        {
            Write(Magic);
        }
    }

    /// <summary>
    /// Creates a writer like the constructor does, writing the start of the file with an asynchronous write. Write the row groups with
    /// <see cref="RowGroupWriter.WriteColumnAsync"/> or <see cref="RowGroupWriter.WriteColumnsAsync(IReadOnlyList{Action{ColumnWriter}}, CancellationToken)"/>,
    /// finish them with <see cref="RowGroupWriter.CloseAsync"/> and the file with <see cref="CloseAsync"/>, and no write to the stream blocks a thread.
    /// </summary>
    /// <exception cref="NotSupportedException">The schema has an INT96 column, or the compression codec is not registered.</exception>
    public static async ValueTask<ParquetFileWriter> CreateAsync(Stream output, ParquetSchema schema, ParquetWriterOptions? options = null, bool leaveOpen = false, CancellationToken cancellationToken = default)
    {
        var writer = new ParquetFileWriter(output, schema, options, leaveOpen, writeMagic: false);
        try
        {
            await writer.WriteAsync(Magic, cancellationToken).ConfigureAwait(false);
            return writer;
        }
        catch
        {
            writer.Abandon(); // no footer for a file that could not be started
            await writer.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ParquetSchema Schema { get; }

    /// <summary>The number of bytes written so far.</summary>
    public long Position { get; private set; }

    internal WriterBuffers Buffers { get; } = new();

    internal bool IsUsable => !_closed && !_abandoned;

    /// <summary>Starts a new row group. The previous one must have been closed or disposed.</summary>
    public RowGroupWriter AppendRowGroup()
    {
        EnsureUsable();
        if (_openGroup is not null)
        {
            throw new InvalidOperationException("The previous row group is still open; close or dispose it first.");
        }

        return _openGroup = new RowGroupWriter(this, _rowGroups.Count);
    }

    /// <summary>Writes the footer and, unless asked to leave it open, closes the stream.</summary>
    /// <exception cref="InvalidOperationException">A row group is still open, or the file was abandoned after an incomplete row group.</exception>
    public void Close()
    {
        if (!BeginClose())
        {
            return;
        }

        WritePageIndexes();
        WriteFooter();
        _output.Flush();
        ReleaseStream();
    }

    /// <summary>
    /// Writes the footer without blocking and, unless asked to leave it open, closes the stream. The page indexes, the footer and the
    /// flush are written to the stream with asynchronous calls, so the thread is free while they are on their way.
    /// </summary>
    /// <exception cref="InvalidOperationException">A row group is still open, or the file was abandoned after an incomplete row group.</exception>
    public async ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (!BeginClose())
        {
            return;
        }

        try
        {
            await WritePageIndexesAsync(cancellationToken).ConfigureAwait(false);
            await WriteFooterAsync(cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseStreamAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Checks that the file can be closed; returns false when it is closed already, and marks it closed otherwise.</summary>
    private bool BeginClose()
    {
        if (_closed)
        {
            return false;
        }

        if (_abandoned)
        {
            throw new InvalidOperationException("A row group was abandoned before all its columns were written, so no valid file can be completed.");
        }

        if (_openGroup is not null)
        {
            throw new InvalidOperationException("A row group is still open; close or dispose it before closing the file.");
        }

        _closed = true;
        return true;
    }

    /// <summary>Completes the file if possible. It does not throw for a file that was abandoned (for example after an exception).</summary>
    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        if (_abandoned || _openGroup is not null)
        {
            _closed = true; // no footer: the file is not valid, and the original problem was reported already
            ReleaseStream();
            return;
        }

        Close();
    }

    /// <summary>Completes the file like <see cref="CloseAsync"/> if possible; it does not throw for a file that was abandoned.</summary>
    public async ValueTask DisposeAsync()
    {
        if (!_closed && !_abandoned && _openGroup is null)
        {
            await CloseAsync().ConfigureAwait(false);
            return;
        }

        Dispose();
    }

    internal ColumnWriter CreateColumnWriter(PrimitiveNode column) => new(this, column, _codec, _options);

    /// <summary>A writer of a column that puts its pages in <paramref name="sink"/> and not in the file, to be copied to the file later.</summary>
    internal ColumnWriter CreateDetachedColumnWriter(PrimitiveNode column, DetachedColumnSink sink, WriterBuffers buffers) => new(sink, buffers, column, _codec, _options);

    internal void OnRowGroupClosed(RowGroup rowGroup)
    {
        _rowGroups.Add(rowGroup);
        _totalRows += rowGroup.NumRows;
        _openGroup = null;
    }

    /// <summary>Takes the page indexes of the columns of a row group, which are written after the last row group.</summary>
    internal void AddPageIndexes(List<(ColumnChunk Chunk, PageIndex Index)> pageIndexes) => _pageIndexes.AddRange(pageIndexes);

    internal void Abandon() => _abandoned = true;

    void IColumnSink.EnsureUsable() => EnsureUsable();

    void IColumnSink.Write(ReadOnlySpan<byte> bytes) => Write(bytes);

    internal void EnsureUsable()
    {
        if (_closed)
        {
            throw new InvalidOperationException("The file writer is already closed.");
        }

        if (_abandoned)
        {
            throw new InvalidOperationException("A row group was abandoned, so this file can no longer be written.");
        }
    }

    internal void Write(ReadOnlySpan<byte> bytes)
    {
        _output.Write(bytes);
        Position += bytes.Length;
    }

    internal async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await _output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        Position += bytes.Length;
    }

    /// <summary>Writes the column indexes of all the columns, then their offset indexes, and records where each is in its column chunk.</summary>
    private void WritePageIndexes()
    {
        var buffer = new ArrayBufferWriter<byte>();
        foreach (var write in PageIndexParts(buffer))
        {
            write();
            Write(buffer.WrittenSpan);
        }

        _pageIndexes.Clear();
    }

    private async ValueTask WritePageIndexesAsync(CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        foreach (var write in PageIndexParts(buffer))
        {
            write();
            await WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
        }

        _pageIndexes.Clear();
    }

    /// <summary>
    /// The page index structures in the order they are written. Running each step serializes one of them into <paramref name="buffer"/>
    /// and records its position and length in the column chunk, so the caller writes the buffer right after.
    /// </summary>
    private IEnumerable<Action> PageIndexParts(ArrayBufferWriter<byte> buffer)
    {
        foreach (var (chunk, index) in _pageIndexes)
        {
            if (index.Columns is null)
            {
                continue;
            }

            yield return () =>
            {
                buffer.ResetWrittenCount();
                index.Columns.Serialize(buffer);
                chunk.ColumnIndexOffset = Position;
                chunk.ColumnIndexLength = buffer.WrittenCount;
            };
        }

        foreach (var (chunk, index) in _pageIndexes)
        {
            yield return () =>
            {
                buffer.ResetWrittenCount();
                index.Offsets.Serialize(buffer);
                chunk.OffsetIndexOffset = Position;
                chunk.OffsetIndexLength = buffer.WrittenCount;
            };
        }
    }

    private void WriteFooter()
    {
        var footer = SerializeFooter();
        Write(footer);
    }

    private async ValueTask WriteFooterAsync(CancellationToken cancellationToken) =>
        await WriteAsync(SerializeFooter(), cancellationToken).ConfigureAwait(false);

    /// <summary>The footer, its length and the closing magic, as one block of bytes.</summary>
    private byte[] SerializeFooter()
    {
        var metadata = new FileMetaData
        {
            Version = 1, // the spec advises writing 1 for the widest compatibility
            Schema = Schema.ToElements(),
            NumRows = _totalRows,
            RowGroups = _rowGroups,
            KeyValueMetadata = _options.KeyValueMetadata?.Select(pair => new KeyValue { Key = pair.Key, Value = pair.Value }).ToList(),
            CreatedBy = _options.CreatedBy ?? DefaultCreatedBy,
            ColumnOrders = Enumerable.Repeat(ColumnOrder.TypeDefined, Schema.Columns.Count).ToList(),
        };

        var buffer = new ArrayBufferWriter<byte>();
        metadata.Serialize(buffer);
        var bytes = new byte[buffer.WrittenCount + sizeof(int) + Magic.Length];
        buffer.WrittenSpan.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(buffer.WrittenCount), buffer.WrittenCount);
        Magic.CopyTo(bytes, buffer.WrittenCount + sizeof(int));
        return bytes;
    }

    private void ReleaseStream()
    {
        Buffers.Dispose();
        if (!_leaveOpen)
        {
            _output.Dispose();
        }
    }

    private async ValueTask ReleaseStreamAsync()
    {
        Buffers.Dispose();
        if (!_leaveOpen)
        {
            await _output.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string DefaultCreatedBy { get; } =
        $"ParquetNeo version {typeof(ParquetFileWriter).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}";

    private static void ValidateSchema(ParquetSchema schema)
    {
        if (schema.Columns.Count == 0)
        {
            throw new ArgumentException("The schema has no columns.", nameof(schema));
        }

        foreach (var column in schema.Columns)
        {
            if (column.Type == PhysicalType.Int96)
            {
                throw new NotSupportedException($"Column '{string.Join('.', column.Path)}' is INT96, a deprecated type that can be read but not written.");
            }
        }
    }
}
