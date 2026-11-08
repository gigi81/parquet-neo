using ParquetNeo.ColumnTypes;
using ParquetNeo.Core;
using ParquetNeo.Exceptions;

namespace ParquetNeo.Serialization.Columns;

/// <summary>
/// The entries of one column of a row group: the values that are present in one array, and a definition level per entry when the
/// column can have missing values (and a repetition level when it is repeated), read in order by taking one entry at a time.
/// </summary>
internal sealed class LeafColumn<TValue>(ColumnType<TValue> type) : ILeafColumn
{
    private const int Batch = 4096;

    private TValue[] _values = [];
    private int[] _definition = [];
    private int[] _repetition = [];
    private int _maxDefinition;
    private int _entries;
    private int _present;
    private int _nextEntry;
    private int _nextValue;
    private bool _missing;

    public string Name { get; private set; } = "";

    public int Definition
    {
        get
        {
            if (_missing)
            {
                return 0;
            }

            CheckNotAtEnd();
            return _maxDefinition == 0 ? 0 : _definition[_nextEntry];
        }
    }

    public int Repetition
    {
        get
        {
            if (_missing)
            {
                return 0;
            }

            CheckNotAtEnd();
            return _repetition.Length == 0 ? 0 : _repetition[_nextEntry];
        }
    }

    public bool AtEnd => _missing || _nextEntry >= _entries;

    public void Fill(ColumnReader? column)
    {
        _entries = 0;
        _present = 0;
        _nextEntry = 0;
        _nextValue = 0;
        _missing = column is null;
        _repetition = [];
        if (column is null)
        {
            return;
        }

        var node = column.Column;
        Name = string.Join('.', node.Path);
        _maxDefinition = node.MaxDefinitionLevel;
        bool definitionLevels = node.MaxDefinitionLevel > 0;
        bool repetitionLevels = node.MaxRepetitionLevel > 0;
        if (repetitionLevels)
        {
            _repetition = new int[Batch];
        }

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

            var read = type.Read(
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

    /// <summary>Takes the next entry: true and its value, or false when it is a null. A column the file lacks has only nulls.</summary>
    /// <exception cref="CorruptColumnException">The column holds fewer entries than the rows need.</exception>
    public bool TryTake(out TValue value)
    {
        value = default!;
        if (_missing)
        {
            return false;
        }

        CheckNotAtEnd();
        int definition = _maxDefinition == 0 ? 0 : _definition[_nextEntry];
        _nextEntry++;
        if (definition != _maxDefinition)
        {
            return false;
        }

        value = _values[_nextValue++];
        return true;
    }

    public void Skip()
    {
        if (_missing)
        {
            return;
        }

        CheckNotAtEnd();
        int definition = _maxDefinition == 0 ? 0 : _definition[_nextEntry];
        _nextEntry++;
        if (definition == _maxDefinition)
        {
            _nextValue++;
        }
    }

    private void CheckNotAtEnd()
    {
        if (_nextEntry >= _entries)
        {
            throw new CorruptColumnException(Name, $"it holds {_entries} entries, fewer than the rows of its row group need");
        }
    }

    private static void Reserve<T>(ref T[] array, int used)
    {
        if (array.Length - used < Batch)
        {
            Array.Resize(ref array, Math.Max(array.Length * 2, used + Batch));
        }
    }
}
