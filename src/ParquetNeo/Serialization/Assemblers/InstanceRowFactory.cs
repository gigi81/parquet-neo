namespace ParquetNeo.Serialization.Assemblers;

/// <summary>A row type with a constructor that takes nothing: an instance is created and its properties set.</summary>
internal sealed class InstanceRowFactory<TRow>(Func<TRow> create) : RowFactory<TRow>
{
    public override bool UsesSlots => false;

    public override void Begin(RowContext<TRow> context) => context.Instance = create();

    public override TRow End(RowContext<TRow> context) => context.Instance;
}
