using ParquetNeo.Core;

namespace ParquetNeo.Data;

/// <summary>The values of one column of a row group, held by row so that a reader can give them in any order.</summary>
internal abstract class ColumnData(string name)
{
    public string Name { get; } = name;

    /// <summary>Reads all the rows of the column chunk.</summary>
    /// <exception cref="Exceptions.CorruptColumnException">The column holds a different number of rows than the row group.</exception>
    public abstract void Fill(ColumnReader reader, int rows);

    public abstract bool IsNull(int row);

    /// <summary>The value as an object: <see cref="DBNull.Value"/> for a null.</summary>
    public abstract object GetValue(int row);
}
