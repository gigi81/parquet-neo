using ParquetNeo.Exceptions;
using ParquetNeo.Schema;
using ParquetNeo.Serialization.Columns;

namespace ParquetNeo.Serialization.Assemblers;

/// <param name="name">The name of the column or field of the file.</param>
/// <param name="slot">The index of the property among the values waiting for a constructor.</param>
/// <param name="set">Sets the property on the instance, or null when the values wait for a constructor.</param>
/// <param name="setNull">Sets the property to null, or null when the values wait for a constructor.</param>
internal sealed class PropertyAssembler<TParent, TValue>(
    string name,
    string description,
    bool optional,
    int slot,
    Assembler<TValue> value,
    Setter<TParent, TValue>? set,
    DefaultSetter<TParent>? setNull) : PropertyAssembler<TParent>
{
    public override void Bind(GroupNode? group, string where, ReadBinder binder)
    {
        var node = group is null ? null : ReadBinder.Find(group, name);
        if (node is null && group is not null && !optional)
        {
            string columns = string.Join(", ", group.Children.Select(c => $"'{c.Name}'"));
            throw ReadBinder.Mismatch($"{where} has no {(group.Parent is null ? "column" : "field")} named '{name}' for {description}, which cannot be null. Its {(group.Parent is null ? "columns" : "fields")} are: {columns}.");
        }

        value.Bind(node, binder);
    }

    public override void AddState(List<ILeafColumn> columns, List<object> objects) => value.AddState(columns, objects);

    public override void Read(ReadContext context, RowContext<TParent> parent)
    {
        if (value.Read(context, out TValue? read))
        {
            if (set is not null)
            {
                set(ref parent.Instance, read);
            }
            else
            {
                parent.Slots![slot] = read;
            }
        }
        else if (!optional)
        {
            throw new ValueConversionException($"{description} cannot be null, but row {context.Row} of the file has a null for it.");
        }
        else
        {
            setNull?.Invoke(ref parent.Instance);
        }
    }
}
