using ParquetNeo.Exceptions;
using ParquetNeo.Serialization.Columns;

namespace ParquetNeo.Serialization.Assemblers;

/// <param name="slot">The index of the property among the values waiting for a constructor.</param>
/// <param name="set">Sets the property on the instance, or null when the values wait for a constructor.</param>
/// <param name="setNull">Sets the property to null, or null when the values wait for a constructor.</param>
internal sealed class PropertyAssembler<TRow, TValue>(
    string description,
    bool optional,
    int slot,
    Assembler<TValue> value,
    Setter<TRow, TValue>? set,
    DefaultSetter<TRow>? setNull) : PropertyAssembler<TRow>
{
    public override void AddColumns(List<ILeafColumn> columns) => value.AddColumns(columns);

    public override void Read(ILeafColumn[] columns, RowContext<TRow> context)
    {
        if (value.Read(columns, out TValue? read))
        {
            if (set is not null)
            {
                set(ref context.Instance, read);
            }
            else
            {
                context.Slots![slot] = read;
            }
        }
        else if (!optional)
        {
            throw new ValueConversionException($"{description} cannot be null, but row {context.Row} of the file has a null for it.");
        }
        else
        {
            setNull?.Invoke(ref context.Instance);
        }
    }
}
