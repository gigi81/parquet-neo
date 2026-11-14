using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using ParquetNeo.ColumnTypes;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Data;

/// <summary>
/// Reads a Parquet file as a <see cref="DbDataReader"/>, so that it can go wherever a query result can: <c>SqlBulkCopy</c>,
/// <see cref="DataTable.Load(IDataReader)"/>, Dapper and the like.
/// </summary>
/// <remarks>
/// <para>
/// The columns are the top-level columns that hold a single value per row (not lists, maps or nested objects), or the ones named
/// in <see cref="ParquetDataReaderOptions.Columns"/>. Each is given as the .NET type its logical type stands for:
/// <see cref="int"/> and <see cref="long"/> for plain integers, <see cref="sbyte"/> to <see cref="ulong"/> for the annotated
/// ones, <see cref="string"/> for text, <see cref="byte"/> arrays for binary, <see cref="decimal"/>, <see cref="DateTime"/>
/// (UTC when the column is adjusted to UTC), <see cref="DateOnly"/>, <see cref="TimeOnly"/>, <see cref="Guid"/>, <see cref="Half"/>,
/// <see cref="float"/>, <see cref="double"/> and <see cref="bool"/>. Binary columns, including an unannotated
/// FIXED_LEN_BYTE_ARRAY, are <see cref="byte"/> arrays. A column that no .NET type fits, such as an INTERVAL, is a
/// <see cref="NotSupportedException"/> unless it is left out with <see cref="ParquetDataReaderOptions.Columns"/>.
/// A null is <see cref="DBNull"/>.
/// </para>
/// <para>
/// A row group is read into memory, column by column, when the reader reaches it, and rows are then given from memory, so a
/// reader holds one row group at a time. The typed getters (<see cref="GetInt32"/> and so on) throw an
/// <see cref="InvalidCastException"/> when the column is of another type or the value is null.
/// </para>
/// </remarks>
public sealed class ParquetDataReader : DbDataReader
{
    private readonly ParquetFileReader _file;
    private readonly ColumnData[] _columns;
    private readonly PrimitiveNode[] _nodes;
    private readonly Type[] _types;
    private readonly Dictionary<string, int> _ordinals;
    private readonly IReadOnlyList<int> _rowGroups;
    private int _rowGroup = -1;
    private int _rowsInGroup;
    private int _row = -1;
    private bool _closed;

    /// <param name="input">A readable, seekable stream holding the whole file.</param>
    /// <param name="options">Which columns to read, and the codecs and limits; <c>null</c> for all and the defaults.</param>
    /// <param name="leaveOpen">Whether to keep <paramref name="input"/> open when the reader is closed.</param>
    /// <exception cref="ArgumentException">A column named in the options is not a column of the file that holds a single value per row.</exception>
    /// <exception cref="NotSupportedException">A column has no .NET type that fits it.</exception>
    public ParquetDataReader(Stream input, ParquetDataReaderOptions? options = null, bool leaveOpen = false)
    {
        options ??= new ParquetDataReaderOptions();
        _file = new ParquetFileReader(input, options.Reader, leaveOpen);
        try
        {
            _rowGroups = options.RowGroupFilter is { } filter ? _file.GetMatchingRowGroups(filter) : Enumerable.Range(0, _file.RowGroupCount).ToArray();
            _nodes = SelectColumns(_file.Schema, options.Columns);
            _columns = new ColumnData[_nodes.Length];
            _types = new Type[_nodes.Length];
            _ordinals = new Dictionary<string, int>(_nodes.Length, StringComparer.Ordinal);
            for (int i = 0; i < _nodes.Length; i++)
            {
                var node = _nodes[i];
                var type = FileColumnTypes.Choose(node)
                    ?? throw new NotSupportedException($"Column '{node.Name}' is stored as {ColumnProblems.Describe(node)}, which no .NET type fits; leave it out with {nameof(ParquetDataReaderOptions)}.{nameof(ParquetDataReaderOptions.Columns)}.");
                _types[i] = type.ValueType;
                _columns[i] = type.Accept(new ColumnDataFactory(node.Name));
                _ordinals.TryAdd(node.Name, i);
            }
        }
        catch
        {
            _file.Dispose();
            throw;
        }
    }

    public override int FieldCount => _columns.Length;

    public override bool HasRows => _file.RowCount > 0;

    public override bool IsClosed => _closed;

    public override int Depth => 0;

    public override int RecordsAffected => -1;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read()
    {
        EnsureOpen();
        while (_row + 1 >= _rowsInGroup)
        {
            if (!LoadNextRowGroup())
            {
                _row = _rowsInGroup;
                return false;
            }
        }

        _row++;
        return true;
    }

    public override bool NextResult() => false;

    public override void Close()
    {
        if (!_closed)
        {
            _closed = true;
            _file.Dispose();
        }
    }

    public override string GetName(int ordinal) => _nodes[ordinal].Name;

    [SuppressMessage("Major Code Smell", "S112:General or reserved exceptions should never be thrown",
        Justification = "IndexOutOfRangeException is what the DbDataReader contract says GetOrdinal throws for an unknown name, and consumers rely on it.")]
    public override int GetOrdinal(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (_ordinals.TryGetValue(name, out int ordinal))
        {
            return ordinal;
        }

        for (int i = 0; i < _nodes.Length; i++)
        {
            if (string.Equals(_nodes[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new IndexOutOfRangeException($"The file has no column named '{name}'.");
    }

    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
    [UnconditionalSuppressMessage("Trimming", "IL2063:Value returned from method can not be statically determined and may not meet DynamicallyAccessedMembers requirements",
        Justification = "The types of the columns are the primitive types of the column types, whose members the caller of GetFieldType does not use by reflection.")]
    public override Type GetFieldType(int ordinal) => _types[ordinal];

    public override string GetDataTypeName(int ordinal) => _types[ordinal].Name;

    public override bool IsDBNull(int ordinal) => _columns[ordinal].IsNull(CurrentRow());

    public override object GetValue(int ordinal) => _columns[ordinal].GetValue(CurrentRow());

    public override int GetValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int count = Math.Min(values.Length, _columns.Length);
        for (int i = 0; i < count; i++)
        {
            values[i] = GetValue(i);
        }

        return count;
    }

    public override bool GetBoolean(int ordinal) => Get<bool>(ordinal);

    public override byte GetByte(int ordinal) => Get<byte>(ordinal);

    public override short GetInt16(int ordinal) => Get<short>(ordinal);

    public override int GetInt32(int ordinal) => Get<int>(ordinal);

    public override long GetInt64(int ordinal) => Get<long>(ordinal);

    public override float GetFloat(int ordinal) => Get<float>(ordinal);

    public override double GetDouble(int ordinal) => Get<double>(ordinal);

    public override decimal GetDecimal(int ordinal) => Get<decimal>(ordinal);

    public override string GetString(int ordinal) => Get<string>(ordinal);

    public override DateTime GetDateTime(int ordinal) => Get<DateTime>(ordinal);

    public override Guid GetGuid(int ordinal) => Get<Guid>(ordinal);

    public override char GetChar(int ordinal) => throw new InvalidCastException($"Column '{GetName(ordinal)}' cannot be read as a char; Parquet has no such type.");

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var bytes = Get<byte[]>(ordinal);
        if (buffer is null)
        {
            return bytes.Length;
        }

        ArgumentOutOfRangeException.ThrowIfNegative(dataOffset);
        if (dataOffset >= bytes.Length)
        {
            return 0;
        }

        int count = (int)Math.Min(length, bytes.Length - dataOffset);
        Array.Copy(bytes, dataOffset, buffer, bufferOffset, count);
        return count;
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        string text = Get<string>(ordinal);
        if (buffer is null)
        {
            return text.Length;
        }

        ArgumentOutOfRangeException.ThrowIfNegative(dataOffset);
        if (dataOffset >= text.Length)
        {
            return 0;
        }

        int count = (int)Math.Min(length, text.Length - dataOffset);
        text.CopyTo((int)dataOffset, buffer, bufferOffset, count);
        return count;
    }

    public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);

    [UnconditionalSuppressMessage("Trimming", "IL2111:Method with DynamicallyAccessedMembersAttribute is accessed via reflection",
        Justification = "DataTable only stores the Type values it is given as data; it does not read their members.")]
    public override DataTable GetSchemaTable()
    {
        EnsureOpen();
        var table = new DataTable("SchemaTable");
        table.Columns.Add("ColumnName", typeof(string));
        table.Columns.Add("ColumnOrdinal", typeof(int));
        table.Columns.Add("ColumnSize", typeof(int));
        table.Columns.Add("NumericPrecision", typeof(short));
        table.Columns.Add("NumericScale", typeof(short));
        table.Columns.Add("DataType", typeof(Type));
        table.Columns.Add("DataTypeName", typeof(string));
        table.Columns.Add("AllowDBNull", typeof(bool));
        for (int i = 0; i < _nodes.Length; i++)
        {
            var node = _nodes[i];
            var decimalType = node.LogicalType is { Kind: LogicalTypeKind.Decimal } logical ? logical : null;
            table.Rows.Add(node.Name, i, -1, (short)(decimalType?.Precision ?? 0), (short)(decimalType?.Scale ?? 0), _types[i], _types[i].Name, node.MaxDefinitionLevel > 0);
        }

        return table;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Close();
        }

        base.Dispose(disposing);
    }

    private static PrimitiveNode[] SelectColumns(ParquetSchema schema, IReadOnlyList<string>? names)
    {
        var single = schema.Columns.Where(c => c.Path.Length == 1 && c.MaxRepetitionLevel == 0).ToList();
        if (names is null)
        {
            return [.. single];
        }

        var selected = new PrimitiveNode[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            selected[i] = single.Find(c => string.Equals(c.Name, names[i], StringComparison.Ordinal))
                ?? throw new ArgumentException($"The file has no column named '{names[i]}' that holds a single value per row.", nameof(names));
        }

        return selected;
    }

    private bool LoadNextRowGroup()
    {
        while (++_rowGroup < _rowGroups.Count)
        {
            var group = _file.OpenRowGroup(_rowGroups[_rowGroup]);
            if (group.RowCount == 0)
            {
                continue;
            }

            int rows = checked((int)group.RowCount);
            for (int i = 0; i < _columns.Length; i++)
            {
                using var reader = group.OpenColumn(_nodes[i].ColumnIndex);
                _columns[i].Fill(reader, rows);
            }

            _rowsInGroup = rows;
            _row = -1;
            return true;
        }

        _rowsInGroup = 0;
        return false;
    }

    private int CurrentRow()
    {
        EnsureOpen();
        return _row >= 0 && _row < _rowsInGroup ? _row : throw new InvalidOperationException("There is no current row; call Read first.");
    }

    private TValue Get<TValue>(int ordinal) =>
        _columns[ordinal] is ColumnData<TValue> column
            ? column.Get(CurrentRow())
            : throw new InvalidCastException($"Column '{GetName(ordinal)}' is a {_types[ordinal].Name}, not a {typeof(TValue).Name}.");

    private void EnsureOpen() => ObjectDisposedException.ThrowIf(_closed, this);

    /// <summary>Makes the data of a column type, with the .NET type of the column type as its type argument.</summary>
    private sealed class ColumnDataFactory(string name) : IColumnTypeVisitor<ColumnData>
    {
        public ColumnData Visit<TValue>(ColumnType<TValue> type) => new ColumnData<TValue>(name, type);
    }
}
