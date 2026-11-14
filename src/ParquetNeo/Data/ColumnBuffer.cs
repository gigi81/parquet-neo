using System.Buffers;
using System.Data;
using System.Runtime.CompilerServices;
using ParquetNeo.ColumnTypes;

namespace ParquetNeo.Data;

/// <summary>
/// The values of one column of a row group: the ones that are present in one list and, when the column can hold nulls, a
/// definition level per row, which is the form <see cref="ColumnWriter"/> takes. The arrays come from the shared array pool and go
/// back to it when the buffer is disposed, instead of lists that double their arrays on the large object heap for every file.
/// </summary>
internal sealed class ColumnBuffer<TValue> : IColumnBuffer
{
    private const int InitialCapacity = 1024;

    private readonly int _ordinal;
    private readonly string _name;
    private readonly ColumnType<TValue> _type;
    private readonly Func<IDataRecord, int, TValue> _getter = ColumnGetters.For<TValue>();
    private readonly bool _optional;
    private TValue[] _values = ArrayPool<TValue>.Shared.Rent(InitialCapacity);
    private int[]? _definitionLevels;
    private int _present;
    private int _entries;

    /// <param name="optional">Whether the column can hold nulls.</param>
    public ColumnBuffer(int ordinal, string name, ColumnType<TValue> type, bool optional)
    {
        _ordinal = ordinal;
        _name = name;
        _type = type;
        _optional = optional;
        _definitionLevels = optional ? ArrayPool<int>.Shared.Rent(InitialCapacity) : null;
    }

    public void Add(IDataRecord row, long rowNumber)
    {
        if (row.IsDBNull(_ordinal))
        {
            if (!_optional)
            {
                throw new InvalidOperationException($"Column '{_name}' is not nullable according to the reader, but row {rowNumber} holds a null.");
            }

            AddLevel(0);
            return;
        }

        if (_optional)
        {
            AddLevel(1);
        }

        if (_present == _values.Length)
        {
            Grow(ref _values, _present, RuntimeHelpers.IsReferenceOrContainsReferences<TValue>());
        }

        _values[_present++] = _getter(row, _ordinal);
    }

    public void WriteAndClear(ColumnWriter writer)
    {
        _type.Write(writer, _values.AsSpan(0, _present), _definitionLevels is null ? default : _definitionLevels.AsSpan(0, _entries));
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
        {
            Array.Clear(_values, 0, _present);
        }

        _present = 0;
        _entries = 0;
    }

    /// <summary>Gives the arrays back to the pool; the buffer cannot be used after this.</summary>
    public void Dispose()
    {
        // References are cleared on the way back, so that the pool does not keep the values of the rows alive.
        ArrayPool<TValue>.Shared.Return(_values, RuntimeHelpers.IsReferenceOrContainsReferences<TValue>());
        _values = [];
        if (_definitionLevels is not null)
        {
            ArrayPool<int>.Shared.Return(_definitionLevels);
            _definitionLevels = null;
        }

        _present = 0;
        _entries = 0;
    }

    private void AddLevel(int level)
    {
        if (_entries == _definitionLevels!.Length)
        {
            Grow(ref _definitionLevels, _entries, clear: false);
        }

        _definitionLevels[_entries++] = level;
    }

    private static void Grow<T>(ref T[] array, int used, bool clear)
    {
        var larger = ArrayPool<T>.Shared.Rent(array.Length * 2);
        array.AsSpan(0, used).CopyTo(larger);
        ArrayPool<T>.Shared.Return(array, clear);
        array = larger;
    }
}
