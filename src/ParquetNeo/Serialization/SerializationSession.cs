using ParquetNeo.Core;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Serialization;

/// <summary>
/// Turns rows into a Parquet file. The file is encoded into memory, one row group at a time, and handed over in pieces
/// (<see cref="Staged"/>), so that the caller can write the pieces to the stream however it likes, synchronously or not.
/// </summary>
internal sealed class SerializationSession<TRow> : IDisposable
{
    private readonly MemoryStream _staging = new();
    private readonly ParquetFileWriter _writer;
    private readonly IColumnBuffer<TRow>[] _buffers;
    private readonly int _rowGroupSize;
    private int _rowsInGroup;

    public SerializationSession(ParquetSerializerOptions? options)
    {
        options ??= new ParquetSerializerOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.RowGroupSize, 1, nameof(options));
        ArgumentNullException.ThrowIfNull(options.Writer, nameof(options));

        var columns = RowModel<TRow>.Columns;
        _rowGroupSize = options.RowGroupSize;
        _buffers = columns.Select(c => c.CreateBuffer(_rowGroupSize)).ToArray();

        var writerOptions = options.Writer;
        var encodings = columns.Where(c => c.Encoding is not null).ToDictionary(c => c.Name, c => c.Encoding!.Value);
        if (encodings.Count > 0)
        {
            foreach (var (column, encoding) in writerOptions.ColumnEncodings ?? new Dictionary<string, ParquetEncoding>() as IReadOnlyDictionary<string, ParquetEncoding>)
            {
                encodings[column] = encoding;
            }

            writerOptions = writerOptions with { ColumnEncodings = encodings };
        }

        _writer = new ParquetFileWriter(_staging, new ParquetSchema(columns.Select(c => c.CreateNode())), writerOptions, leaveOpen: true);
    }

    /// <summary>Whether the row group holds as many rows as the options ask for.</summary>
    public bool IsFull => _rowsInGroup >= _rowGroupSize;

    /// <summary>The bytes of the file encoded so far and not yet taken: call <see cref="ResetStaged"/> once they are written.</summary>
    public ReadOnlyMemory<byte> Staged => _staging.GetBuffer().AsMemory(0, (int)_staging.Length);

    public void ResetStaged() => _staging.SetLength(0);

    public void Add(TRow row)
    {
        ArgumentNullException.ThrowIfNull(row, nameof(row));
        foreach (var buffer in _buffers)
        {
            buffer.Append(row);
        }

        _rowsInGroup++;
    }

    /// <summary>Encodes the rows collected so far as a row group.</summary>
    public void CloseRowGroup()
    {
        if (_rowsInGroup == 0)
        {
            return;
        }

        using (var group = _writer.AppendRowGroup())
        {
            foreach (var buffer in _buffers)
            {
                buffer.WriteAndClear(group.NextColumn());
            }
        }

        _rowsInGroup = 0;
    }

    /// <summary>Encodes the last row group and the footer.</summary>
    public void Finish()
    {
        CloseRowGroup();
        _writer.Close();
    }

    public void Dispose()
    {
        _writer.Dispose();
        _staging.Dispose();
    }
}
