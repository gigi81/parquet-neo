namespace ParquetNeo.Serialization.Assemblers;

/// <summary>A row type with a constructor that takes nothing: an instance is created and its properties set.</summary>
internal sealed class InstanceRowFactory<TRow>(Func<TRow> create) : IRowFactory<TRow>
{
    public bool UsesSlots => false;

    public void Begin(RowContext<TRow> context) => context.Instance = create();

    public TRow End(RowContext<TRow> context) => context.Instance;
}
