namespace ParquetNeo.Serialization.Assemblers;

/// <summary>The values of the properties of an object that is built by a constructor, waiting until all of them have been read.</summary>
internal sealed class RowContext
{
    /// <summary>The values of the properties, boxed, in the order of the slots.</summary>
    public object?[]? Slots;
}
