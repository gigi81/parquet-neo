namespace ParquetNeo.Serialization.Assemblers;

internal abstract class Assembler<TValue>(int firstColumn, int columnCount, string description) : Assembler(firstColumn, columnCount, description)
{
    /// <summary>Takes the entries of the next value from the columns; false when the value is null.</summary>
    public sealed override IPropertyAssembler CreateProperty(string name, string description, bool optional, int slot) =>
        new PropertyAssembler<TValue>(name, description, optional, slot, this, property: null);

    public abstract bool Read(ReadContext context, out TValue value);
}
