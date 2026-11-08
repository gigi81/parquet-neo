using ParquetNeo.Core;
using ParquetNeo.Exceptions;

namespace ParquetNeo.Serialization.Columns;

/// <summary>
/// The entries of one column of a row group: the values that are present in one array, and a definition level per entry when the
/// column can have missing values (and a repetition level when it is repeated), read in order by taking one entry at a time.
/// </summary>
internal sealed class LeafColumn<TStored>(BatchReader<TStored> reader) : ILeafColumn
{
    private const int Batch = 4096;

    private TStored[] _values = [];
    private int[] _definition = [];
    private int[] _repetition = [];
    private int _maxDefinition;
    private int _entries;
    private int _present;
    private int _nextEntry;
    private int _nextValue;
    private bool _missing;

    public string Name { get; private set; } = "";

    public void Fill(ColumnReader? column, long rows)
    {
        _entries = 0;
        _present = 0;
        _nextEntry = 0;
        _nextValue = 0;
        _missing = column is null;
        if (column is null)
        {
            _entries = checked((int)rows);
            return;
        }

        var node = column.Column;
        Name = string.Join('.', node.Path);
        _maxDefinition = node.MaxDefinitionLevel;
        bool definitionLevels = node.MaxDefinitionLevel > 0;
        bool repetitionLevels = node.MaxRepetitionLevel > 0;
        while (true)
        {
            Reserve(ref _values, _present);
            if (definitionLevels)
            {
                Reserve(ref _definition, _entries);
            }

            if (repetitionLevels)
            {
                Reserve(ref _repetition, _entries);
            }

            var read = reader(
                column,
                _values.AsSpan(_present, Batch),
                definitionLevels ? _definition.AsSpan(_entries, Batch) : default,
                repetitionLevels ? _repetition.AsSpan(_entries, Batch) : default);
            if (read.Entries == 0)
            {
                break;
            }

            _entries += read.Entries;
            _present += read.Values;
        }
    }

    /// <summary>Takes the next entry: true and its value, or false when it is a null.</summary>
    /// <exception cref="CorruptColumnException">The column holds fewer entries than the row group has rows.</exception>
    public bool TryTake(out TStored value)
    {
        if (_nextEntry >= _entries)
        {
            throw new CorruptColumnException(Name, $"it holds {_entries} entries, fewer than the rows of its row group");
        }

        if (_missing)
        {
            _nextEntry++;
            value = default!;
            return false;
        }

        int definition = _maxDefinition == 0 ? 0 : _definition[_nextEntry];
        _nextEntry++;
        if (definition != _maxDefinition)
        {
            value = default!;
            return false;
        }

        value = _values[_nextValue++];
        return true;
    }

    private static void Reserve<T>(ref T[] array, int used)
    {
        if (array.Length - used < Batch)
        {
            Array.Resize(ref array, Math.Max(array.Length * 2, used + Batch));
        }
    }
}
