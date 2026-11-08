using ParquetNeo.ColumnTypes;
using ParquetNeo.Core;
using ParquetNeo.Exceptions;

namespace ParquetNeo.Data;

/// <summary>The values of one column of a row group, read as <typeparamref name="TValue"/>.</summary>
internal sealed class ColumnData<TValue>(string name, ColumnType<TValue> type) : ColumnData(name)
{
    private const int Batch = 4096;

    private TValue[] _values = [];
    private bool[]? _nulls;

    public override void Fill(ColumnReader reader, int rows)
    {
        var column = reader.Column;
        int maxDefinition = column.MaxDefinitionLevel;
        if (_values.Length < rows)
        {
            _values = new TValue[rows];
        }

        _nulls = maxDefinition > 0 ? new bool[rows] : null;
        var batch = new TValue[Batch];
        var levels = maxDefinition > 0 ? new int[Batch] : [];
        int row = 0;
        while (true)
        {
            var read = type.Read(reader, batch, levels);
            if (read.Entries == 0)
            {
                break;
            }

            if (row + read.Entries > rows)
            {
                throw new CorruptColumnException(Name, $"it holds more entries than the {rows} rows of its row group");
            }

            int next = 0;
            for (int entry = 0; entry < read.Entries; entry++, row++)
            {
                if (maxDefinition == 0 || levels[entry] == maxDefinition)
                {
                    _values[row] = batch[next++];
                }
                else
                {
                    _values[row] = default!;
                    _nulls![row] = true;
                }
            }
        }

        if (row != rows)
        {
            throw new CorruptColumnException(Name, $"it holds {row} entries, but its row group has {rows} rows");
        }
    }

    public override bool IsNull(int row) => _nulls is not null && _nulls[row];

    public override object GetValue(int row) => IsNull(row) ? DBNull.Value : _values[row]!;

    /// <summary>The value of a row, which must not be null.</summary>
    public TValue Get(int row) => IsNull(row) ? throw new InvalidCastException($"Column '{Name}' is null in this row.") : _values[row];
}
