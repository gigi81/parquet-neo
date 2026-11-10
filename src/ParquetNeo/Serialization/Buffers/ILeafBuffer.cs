using ParquetNeo.Core;

namespace ParquetNeo.Serialization.Buffers;

/// <summary>Collects the entries of one column for a row group.</summary>
internal interface ILeafBuffer : IDisposable
{
    /// <summary>Adds the entry of a value that is missing: <paramref name="definition"/> says how many of its optional and repeated ancestors are present.</summary>
    void AddMissing(int definition, int repetition);

    /// <summary>Writes the collected entries to the column and empties the buffer for the next row group.</summary>
    void WriteAndClear(ColumnWriter writer);
}
