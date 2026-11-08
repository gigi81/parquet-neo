namespace ParquetNeo.Serialization.Assemblers;

/// <summary>An object being built: the instance itself or, for types built by a constructor, the values waiting for it.</summary>
internal sealed class RowContext<TRow>
{
    public TRow Instance = default!;

    /// <summary>The values of the properties, boxed, for a type that is built by a constructor.</summary>
    public object?[]? Slots;
}
