using ParquetNeo.Compression;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;
using System.Buffers;
using System.Buffers.Binary;

namespace ParquetNeo.Core;

/// <summary>
/// Writes a Parquet file with flat (non-nested) columns, one row group at a time. The stream only needs to be
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
public sealed class ParquetFileWriter : IDisposable, IAsyncDisposable
{
    private static readonly byte[] Magic = "PAR1"u8.ToArray();

    private readonly Stream _output;
    private readonly bool _leaveOpen;
    private readonly ParquetWriterOptions _options;
    private readonly ICodec _codec;
    private readonly List<RowGroup> _rowGroups = new();
    private RowGroupWriter? _openGroup;
    private long _totalRows;
    private bool _closed;
    private bool _abandoned;

    /// <param name="output">A writable stream, positioned where the file should start.</param>
    /// <param name="schema">The schema; every column must be a direct, non-repeated child of the root (nested data comes later).</param>
    /// <param name="options">Compression, page size and footer settings; <c>null</c> for the defaults.</param>
    /// <param name="leaveOpen">Whether to keep <paramref name="output"/> open when the writer is closed.</param>
    /// <exception cref="NotSupportedException">The schema is nested, has an INT96 column, or the compression codec is not registered.</exception>
    public ParquetFileWriter(Stream output, ParquetSchema schema, ParquetWriterOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(schema);
        if (!output.CanWrite)
        {
            throw new ArgumentException("The stream must be writable.", nameof(output));
        }

        _options = options ?? new ParquetWriterOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.DataPageSize, 1, nameof(options));
        ValidateSchema(schema);

        _output = output;
        _leaveOpen = leaveOpen;
        Schema = schema;
        _codec = _options.Codecs.Get(_options.Compression);

        Write(Magic);
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
        if (_closed)
        {
            return;
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
        WriteFooter();
        _output.Flush();
        ReleaseStream();
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

    public async ValueTask DisposeAsync()
    {
        if (!_closed && !_abandoned && _openGroup is null)
        {
            _closed = true;
            WriteFooter();
            await _output.FlushAsync().ConfigureAwait(false);
            if (!_leaveOpen)
            {
                await _output.DisposeAsync().ConfigureAwait(false);
            }

            return;
        }

        Dispose();
    }

    internal ColumnWriter CreateColumnWriter(PrimitiveNode column) => new(this, column, _codec, _options);

    internal void OnRowGroupClosed(RowGroup rowGroup)
    {
        _rowGroups.Add(rowGroup);
        _totalRows += rowGroup.NumRows;
        _openGroup = null;
    }

    internal void Abandon() => _abandoned = true;

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

    private void WriteFooter()
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
        Write(buffer.WrittenSpan);

        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, buffer.WrittenCount);
        Write(length);
        Write(Magic);
    }

    private void ReleaseStream()
    {
        if (!_leaveOpen)
        {
            _output.Dispose();
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
            if (!ReferenceEquals(column.Parent, schema.Root) || column.Repetition == FieldRepetitionType.Repeated)
            {
                throw new NotSupportedException(
                    $"Column '{string.Join('.', column.Path)}' is nested or repeated; this writer supports only flat columns so far.");
            }

            if (column.Type == PhysicalType.Int96)
            {
                throw new NotSupportedException($"Column '{column.Name}' is INT96, a deprecated type that can be read but not written.");
            }
        }
    }
}
