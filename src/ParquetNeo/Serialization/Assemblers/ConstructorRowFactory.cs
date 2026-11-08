namespace ParquetNeo.Serialization.Assemblers;

/// <summary>A row type built by a constructor whose parameters are properties, such as a record: the values are collected, then passed together.</summary>
internal sealed class ConstructorRowFactory<TRow>(int slotCount, Func<object?[], TRow> build) : RowFactory<TRow>
{
    public override bool UsesSlots => true;

    public override void Begin(RowContext<TRow> context)
    {
        if (context.Slots is null)
        {
            context.Slots = new object?[slotCount];
        }
        else
        {
            Array.Clear(context.Slots);
        }
    }

    public override TRow End(RowContext<TRow> context) => build(context.Slots!);
}
