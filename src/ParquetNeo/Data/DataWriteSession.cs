using System.Data;
using ParquetNeo.ColumnTypes;
using ParquetNeo.Core;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Data;

/// <summary>
/// Turns the rows of a reader into a Parquet file. The file is encoded into memory, one row group at a time, and handed over in
/// pieces (<see cref="Staged"/>), so that the caller can write the pieces to the stream however it likes, synchronously or not.
/// </summary>
internal sealed class DataWriteSession : IDisposable
{
    /// <summary>The largest precision a Parquet decimal is written with here; providers say 255 for a decimal without one.</summary>
    private const int MaxPrecision = 38;

    private readonly MemoryStream _staging = new();
    private readonly ParquetFileWriter _writer;
    private readonly IColumnBuffer[] _buffers;
    private readonly int _rowGroupSize;
    private readonly int _degreeOfParallelism;
    private int _rowsInGroup;
    private long _totalRows;

    /// <exception cref="ArgumentException">The reader has no columns.</exception>
    /// <exception cref="NotSupportedException">A column has a type that cannot be written.</exception>
    public DataWriteSession(IDataReader source, ParquetDataWriterOptions? options)
    {
        options ??= new ParquetDataWriterOptions();
        if (options.RowGroupSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.RowGroupSize, "The row group size must be at least 1.");
        }

        if (options.Writer is null)
        {
            throw new ArgumentNullException(nameof(options), "The writer options must not be null.");
        }

        if (source.FieldCount == 0)
        {
            throw new ArgumentException("The reader has no columns.", nameof(source));
        }

        if (options.MaxDegreeOfParallelism < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxDegreeOfParallelism, "The degree of parallelism must be at least 1.");
        }

        _rowGroupSize = options.RowGroupSize;
        _degreeOfParallelism = options.MaxDegreeOfParallelism;
        var schemaTable = TryGetSchemaTable(source);
        var nodes = new List<SchemaNode>(source.FieldCount);
        _buffers = new IColumnBuffer[source.FieldCount];
        for (int i = 0; i < source.FieldCount; i++)
        {
            string name = source.GetName(i);
            var fieldType = source.GetFieldType(i);
            var columnType = ColumnTypeRegistry.TryGet(fieldType)
                ?? throw new NotSupportedException($"Column '{name}' has the type {fieldType}, which cannot be written as a Parquet column.");
            if (SettingsOf(schemaTable, i, fieldType, options.ColumnSettings, name) is { } settings)
            {
                columnType = columnType.WithSettings(settings);
            }

            bool optional = AllowsNull(schemaTable, i);
            nodes.Add(columnType.CreateNode(name, optional ? FieldRepetitionType.Optional : FieldRepetitionType.Required));
            _buffers[i] = CreateBuffer(columnType, i, name, optional);
        }

        _writer = new ParquetFileWriter(_staging, new ParquetSchema(nodes), options.Writer, leaveOpen: true);
    }

    /// <summary>Whether the row group holds as many rows as the options ask for.</summary>
    public bool IsFull => _rowsInGroup >= _rowGroupSize;

    /// <summary>The bytes of the file encoded so far and not yet taken: call <see cref="ResetStaged"/> once they are written.</summary>
    public ReadOnlyMemory<byte> Staged => _staging.GetBuffer().AsMemory(0, (int)_staging.Length);

    public void ResetStaged() => _staging.SetLength(0);

    public void Add(IDataRecord row)
    {
        foreach (var buffer in _buffers)
        {
            buffer.Add(row, _totalRows);
        }

        _rowsInGroup++;
        _totalRows++;
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
            if (_degreeOfParallelism > 1 && _buffers.Length > 1)
            {
                group.WriteColumns([.. _buffers.Select(buffer => (Action<ColumnWriter>)buffer.WriteAndClear)], _degreeOfParallelism);
            }
            else
            {
                foreach (var buffer in _buffers)
                {
                    buffer.WriteAndClear(group.NextColumn());
                }
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

    private static DataTable? TryGetSchemaTable(IDataReader source)
    {
        try
        {
            return source.GetSchemaTable();
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Whether the column can hold nulls: what the reader says, and that it can when it says nothing.</summary>
    private static bool AllowsNull(DataTable? schemaTable, int ordinal) =>
        schemaTable is null
        || ordinal >= schemaTable.Rows.Count
        || !schemaTable.Columns.Contains("AllowDBNull")
        || schemaTable.Rows[ordinal]["AllowDBNull"] is not bool allowed
        || allowed;

    /// <summary>The settings of a column: the ones in the options, else the precision and scale of a decimal that the reader states.</summary>
    private static ColumnSettings? SettingsOf(DataTable? schemaTable, int ordinal, Type fieldType, IReadOnlyDictionary<string, ColumnSettings>? settings, string name)
    {
        if (settings is not null && settings.TryGetValue(name, out var given))
        {
            return given;
        }

        if (fieldType != typeof(decimal) || schemaTable is null || ordinal >= schemaTable.Rows.Count
            || !schemaTable.Columns.Contains("NumericPrecision") || !schemaTable.Columns.Contains("NumericScale"))
        {
            return null;
        }

        var row = schemaTable.Rows[ordinal];
        if (row["NumericPrecision"] is not (short or int or byte) || row["NumericScale"] is not (short or int or byte))
        {
            return null;
        }

        int precision = Convert.ToInt32(row["NumericPrecision"], System.Globalization.CultureInfo.InvariantCulture);
        int scale = Convert.ToInt32(row["NumericScale"], System.Globalization.CultureInfo.InvariantCulture);
        bool known = precision is >= 1 and <= MaxPrecision && scale is >= 0 and <= DecimalConversion.MaxScale && scale <= precision;
        return known ? new ColumnSettings { Precision = precision, Scale = scale } : null;
    }

    private static IColumnBuffer CreateBuffer(ColumnType columnType, int ordinal, string name, bool optional) =>
        (IColumnBuffer)Activator.CreateInstance(typeof(ColumnBuffer<>).MakeGenericType(columnType.ValueType), ordinal, name, columnType, optional)!;
}
