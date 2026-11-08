using System.Runtime.CompilerServices;
using ParquetNeo.ColumnTypes;
using ParquetNeo.Core;

namespace ParquetNeo.Serialization.Buffers;

/// <summary>
/// Collects the entries of one column for a row group: the values that are present in one array and, when the column can have
/// missing values or lists, a definition level per entry and a repetition level per entry when it is repeated. That is the form
/// <see cref="ColumnWriter"/> takes.
/// </summary>
internal sealed class LeafBuffer<TValue> : ILeafBuffer
{
    private const int InitialCapacity = 256;

    private readonly ColumnType<TValue> _type;
    private TValue[] _values = new TValue[InitialCapacity];
    private int[]? _definition;
    private int[]? _repetition;
    private int _present;
    private int _entries;

    public LeafBuffer(bool hasDefinitionLevels, bool hasRepetitionLevels, ColumnType<TValue> type)
    {
        _type = type;
        _definition = hasDefinitionLevels ? new int[InitialCapacity] : null;
        _repetition = hasRepetitionLevels ? new int[InitialCapacity] : null;
    }

    public void AddValue(TValue value, int definition, int repetition)
    {
        if (_present == _values.Length)
        {
            Array.Resize(ref _values, _values.Length * 2);
        }

        _values[_present++] = value;
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
                Array.Resize(ref _definition, _definition.Length * 2);
            }

            _definition[_entries] = definition;
        }

        if (_repetition is not null)
        {
            if (_entries == _repetition.Length)
            {
                Array.Resize(ref _repetition, _repetition.Length * 2);
            }

            _repetition[_entries] = repetition;
        }

        _entries++;
    }
}
