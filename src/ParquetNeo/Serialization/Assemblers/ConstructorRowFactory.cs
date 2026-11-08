namespace ParquetNeo.Serialization.Assemblers;

/// <summary>A row type built by a constructor whose parameters are properties, such as a record: the values are collected, then passed together.</summary>
internal sealed class ConstructorRowFactory<TRow>(int slotCount, Func<object?[], TRow> build) : IRowFactory<TRow>
{
    public bool UsesSlots => true;

    public void Begin(RowContext<TRow> context)
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

    public TRow End(RowContext<TRow> context) => build(context.Slots!);
}
