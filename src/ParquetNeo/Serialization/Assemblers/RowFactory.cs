namespace ParquetNeo.Serialization.Assemblers;

/// <summary>Creates the rows: either an instance whose properties are then set, or an instance that a constructor builds from all of them.</summary>
internal abstract class RowFactory<TRow>
{
    /// <summary>Whether the values wait in <see cref="RowContext{TRow}.Slots"/> instead of being set on the instance.</summary>
    public abstract bool UsesSlots { get; }

    public abstract void Begin(RowContext<TRow> context);

    public abstract TRow End(RowContext<TRow> context);
}
