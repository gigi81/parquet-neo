namespace ParquetNeo.Serialization.Assemblers;

/// <summary>Creates the rows: either an instance whose properties are then set, or an instance that a constructor builds from all of them.</summary>
internal interface IRowFactory<TRow>
{
    /// <summary>Whether the values wait in <see cref="RowContext{TRow}.Slots"/> instead of being set on the instance.</summary>
    bool UsesSlots { get; }

    void Begin(RowContext<TRow> context);

    TRow End(RowContext<TRow> context);
}
