using System.Data;
using ParquetNeo.Core;

namespace ParquetNeo.Data;

/// <summary>Collects the values of one column of the rows of a reader for a row group, whatever their type.</summary>
internal interface IColumnBuffer
{
    /// <summary>Takes the value of the column from the current row.</summary>
    /// <exception cref="InvalidOperationException">The value is null, but the column is required.</exception>
    void Add(IDataRecord row, long rowNumber);

    /// <summary>Writes the collected values to the column and empties the buffer for the next row group.</summary>
    void WriteAndClear(ColumnWriter writer);
}
