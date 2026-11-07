using ParquetNeo.Core;

namespace ParquetNeo.Serialization;

/// <summary>Collects the values of one column from the rows of a row group.</summary>
internal interface IColumnBuffer<in TRow>
{
    void Append(TRow row);

    /// <summary>Writes the collected values to the column and empties the buffer for the next row group.</summary>
    void WriteAndClear(ColumnWriter writer);
}
