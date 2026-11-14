using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ParquetNeo.ColumnTypes;

namespace ParquetNeo.Serialization.Buffers;

/// <summary>
/// Collects the entries of one column for a row group: the values that are present in one array and, when the column can have
/// missing values or lists, a definition level per entry and a repetition level per entry when it is repeated. That is the form
/// <see cref="ColumnWriter"/> takes.
/// </summary>
/// <remarks>
/// The arrays come from the shared array pool and go back to it when the buffer is disposed: a row group of a million rows takes
/// tens of megabytes of arrays that would be allocated on the large object heap and collected, with a full garbage collection, for
/// every file written. The pool gives out arrays that are not cleared and may be larger than asked for, so nothing here depends on
/// the arrays being empty or of an exact length.
/// </remarks>
internal sealed class LeafBuffer<TValue> : ILeafBuffer
{
    private readonly ColumnType<TValue> _type;
    private TValue[] _values;
    private int[]? _definition;
    private int[]? _repetition;
    private int _present;
    private int _entries;

    /// <param name="capacity">The number of entries to make room for; the buffer grows when there are more.</param>
    public LeafBuffer(bool hasDefinitionLevels, bool hasRepetitionLevels, ColumnType<TValue> type, int capacity)
    {
        capacity = Math.Max(capacity, 1);
        _type = type;
        _values = ArrayPool<TValue>.Shared.Rent(capacity);
        _definition = hasDefinitionLevels ? ArrayPool<int>.Shared.Rent(capacity) : null;
        _repetition = hasRepetitionLevels ? ArrayPool<int>.Shared.Rent(capacity) : null;
    }

    public void AddValue(TValue value, int definition, int repetition)
    {
        if (_present == _values.Length)
        {
            Grow(ref _values, _present, clear: RuntimeHelpers.IsReferenceOrContainsReferences<TValue>());
        }

        // The array is exactly TValue[], so the store needs no check of the array's actual element type (which a reference type would get).
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_values), _present++) = value;
        AddLevels(definition, repetition);
    }

    public void AddMissing(int definition, int repetition) => AddLevels(definition, repetition);

    public void WriteAndClear(ColumnWriter writer)
    {
        _type.Write(writer, _values.AsSpan(0, _present), _definition is null ? default : _definition.AsSpan(0, _entries), _repetition is null ? default : _repetition.AsSpan(0, _entries));
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
        {
            Array.Clear(_values, 0, _present);
        }

        _present = 0;
        _entries = 0;
    }

    private void AddLevels(int definition, int repetition)
    {
        if (_definition is not null)
        {
            if (_entries == _definition.Length)
            {
                Grow(ref _definition, _entries, clear: false);
            }

            _definition[_entries] = definition;
        }

        if (_repetition is not null)
        {
            if (_entries == _repetition.Length)
            {
                Grow(ref _repetition, _entries, clear: false);
            }

            _repetition[_entries] = repetition;
        }

        _entries++;
    }

    /// <summary>Gives the arrays back to the pool; the buffer cannot be used after this.</summary>
    public void Dispose()
    {
        // References are cleared on the way back, so that the pool does not keep the objects of the rows alive.
        ArrayPool<TValue>.Shared.Return(_values, RuntimeHelpers.IsReferenceOrContainsReferences<TValue>());
        _values = [];
        if (_definition is not null)
        {
            ArrayPool<int>.Shared.Return(_definition);
            _definition = [];
        }

        if (_repetition is not null)
        {
            ArrayPool<int>.Shared.Return(_repetition);
            _repetition = [];
        }

        _present = 0;
        _entries = 0;
    }

    /// <summary>Replaces <paramref name="array"/> with one twice as long that holds the <paramref name="used"/> items it had, and returns the old one to the pool.</summary>
    private static void Grow<T>(ref T[] array, int used, bool clear)
    {
        var larger = ArrayPool<T>.Shared.Rent(array.Length * 2);
        array.AsSpan(0, used).CopyTo(larger);
        ArrayPool<T>.Shared.Return(array, clear);
        array = larger;
    }
}
