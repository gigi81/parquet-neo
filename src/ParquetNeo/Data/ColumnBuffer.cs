using System.Data;
using System.Runtime.InteropServices;
using ParquetNeo.ColumnTypes;
using ParquetNeo.Core;

namespace ParquetNeo.Data;

/// <summary>
/// The values of one column of a row group: the ones that are present in one list and, when the column can hold nulls, a
/// definition level per row, which is the form <see cref="ColumnWriter"/> takes.
/// </summary>
internal sealed class ColumnBuffer<TValue> : IColumnBuffer
{
    private readonly int _ordinal;
    private readonly string _name;
    private readonly ColumnType<TValue> _type;
    private readonly Func<IDataRecord, int, TValue> _getter = ColumnGetters.For<TValue>();
    private readonly List<TValue> _values = [];
    private readonly List<int>? _definitionLevels;

    /// <param name="optional">Whether the column can hold nulls.</param>
    public ColumnBuffer(int ordinal, string name, ColumnType<TValue> type, bool optional)
    {
        _ordinal = ordinal;
        _name = name;
        _type = type;
        _definitionLevels = optional ? [] : null;
    }

    public void Add(IDataRecord row, long rowNumber)
    {
        if (row.IsDBNull(_ordinal))
        {
            if (_definitionLevels is null)
            {
                throw new InvalidOperationException($"Column '{_name}' is not nullable according to the reader, but row {rowNumber} holds a null.");
            }

            _definitionLevels.Add(0);
            return;
        }

        _definitionLevels?.Add(1);
        _values.Add(_getter(row, _ordinal));
    }

    public void WriteAndClear(ColumnWriter writer)
    {
        _type.Write(writer, CollectionsMarshal.AsSpan(_values), _definitionLevels is null ? default : CollectionsMarshal.AsSpan(_definitionLevels));
        _values.Clear();
        _definitionLevels?.Clear();
    }
}
