using System.Runtime.CompilerServices;
using ParquetNeo.Core;

namespace ParquetNeo.Serialization;

/// <summary>
/// Collects the values of one column for a row group. The values that are present go in one array and, for an optional
/// column, a definition level per row says whether the row has one; that is the form <see cref="ColumnWriter"/> takes.
/// </summary>
internal sealed class ColumnBuffer<TRow, TValue> : IColumnBuffer<TRow>
{
    private const int InitialCapacity = 1024;

    private readonly TypedColumnDefinition<TRow, TValue> _definition;
    private TValue[] _values;
    private int[]? _levels;
    private int _present;
    private int _rows;
    private long _totalRows;

    public ColumnBuffer(TypedColumnDefinition<TRow, TValue> definition, int rowGroupSize)
    {
        _definition = definition;
        int capacity = Math.Min(rowGroupSize, InitialCapacity);
        _values = new TValue[capacity];
        _levels = definition.Optional ? new int[capacity] : null;
    }

    public void Append(TRow row)
    {
        bool present = _definition.Getter(row, out TValue value);
        if (!present && _levels is null)
        {
            throw new ArgumentException($"{_definition.Property} is null in row {_totalRows}, but its column is required. Make the property nullable to store nulls.");
        }

        if (_rows == _values.Length)
        {
            Array.Resize(ref _values, _values.Length * 2);
            if (_levels is not null)
            {
                Array.Resize(ref _levels, _values.Length);
            }
        }

        if (present)
        {
            _values[_present++] = value;
        }

        if (_levels is not null)
        {
            _levels[_rows] = present ? 1 : 0;
        }

        _rows++;
        _totalRows++;
    }

    public void WriteAndClear(ColumnWriter writer)
    {
        _definition.Writer(writer, _values.AsSpan(0, _present), _levels is null ? default : _levels.AsSpan(0, _rows));
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
        {
            Array.Clear(_values, 0, _present);
        }

        _present = 0;
        _rows = 0;
    }
}
