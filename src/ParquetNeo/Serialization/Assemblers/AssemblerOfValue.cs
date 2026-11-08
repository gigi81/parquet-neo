using ParquetNeo.Serialization.Columns;

namespace ParquetNeo.Serialization.Assemblers;

internal abstract class Assembler<TValue> : Assembler
{
    /// <summary>Takes the entries of the next value from the columns; false when the value is null.</summary>
    public abstract bool Read(ILeafColumn[] columns, out TValue value);
}
