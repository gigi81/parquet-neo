using System.Runtime.CompilerServices;
using ParquetNeo.Core;

namespace ParquetNeo.Serialization;

/// <summary>
/// Collects the entries of one column for a row group: the values that are present in one array and, when the column can have
/// missing values or lists, a definition level per entry and a repetition level per entry when it is repeated. That is the form
/// <see cref="ColumnWriter"/> takes.
/// </summary>
internal sealed class LeafBuffer<TStored> : ILeafBuffer
{
    private const int InitialCapacity = 256;

    private readonly BatchWriter<TStored> _writer;
    private TStored[] _values = new TStored[InitialCapacity];
    private int[]? _definition;
    private int[]? _repetition;
    private int _present;
    private int _entries;

    public LeafBuffer(bool hasDefinitionLevels, bool hasRepetitionLevels, BatchWriter<TStored> writer)
    {
        _writer = writer;
        _definition = hasDefinitionLevels ? new int[InitialCapacity] : null;
        _repetition = hasRepetitionLevels ? new int[InitialCapacity] : null;
    }

    public void AddValue(TStored value, int definition, int repetition)
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
        _writer(writer, _values.AsSpan(0, _present), _definition is null ? default : _definition.AsSpan(0, _entries), _repetition is null ? default : _repetition.AsSpan(0, _entries));
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TStored>())
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
