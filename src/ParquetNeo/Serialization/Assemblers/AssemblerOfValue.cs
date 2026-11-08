namespace ParquetNeo.Serialization.Assemblers;

internal abstract class Assembler<TValue>(int firstColumn, int columnCount, string description) : Assembler(firstColumn, columnCount, description)
{
    /// <summary>Takes the entries of the next value from the columns; false when the value is null.</summary>
    public abstract bool Read(ReadContext context, out TValue value);
}
