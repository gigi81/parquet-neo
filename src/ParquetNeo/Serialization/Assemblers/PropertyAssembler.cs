using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using ParquetNeo.Exceptions;
using ParquetNeo.Schema;
using ParquetNeo.Serialization.Columns;

namespace ParquetNeo.Serialization.Assemblers;

/// <param name="name">The name of the column or field of the file.</param>
/// <param name="slot">The index of the property among the values waiting for a constructor.</param>
/// <param name="property">The property, which the code made by reflection for an object created without a constructor sets; null when a generated mapping supplies that code.</param>
internal sealed class PropertyAssembler<TValue>(
    string name,
    string description,
    bool optional,
    int slot,
    Assembler<TValue> value,
    PropertyInfo? property) : IPropertyAssembler
{
    public void Bind(GroupNode? group, string where, ReadBinder binder)
    {
        var node = group is null ? null : ReadBinder.Find(group, name);
        if (node is null && group is not null && !optional)
        {
            string columns = string.Join(", ", group.Children.Select(c => $"'{c.Name}'"));
            throw ReadBinder.Mismatch($"{where} has no {(group.Parent is null ? "column" : "field")} named '{name}' for {description}, which cannot be null. Its {(group.Parent is null ? "columns" : "fields")} are: {columns}.");
        }

        value.Bind(node, binder);
    }

    public void AddState(List<ILeafColumn> columns, List<object> objects) => value.AddState(columns, objects);

    public void Read(ReadContext context, RowContext parent)
    {
        if (value.Read(context, out TValue? read))
        {
            parent.Slots![slot] = read;
        }
        else if (!optional)
        {
            throw new ValueConversionException($"{description} cannot be null, but row {context.Row} of the file has a null for it.");
        }
    }

    [RequiresUnreferencedCode("Sets the property by reflection.")]
    [RequiresDynamicCode("Builds code to be compiled at run time.")]
    public Expression EmitFlat(ParameterExpression row, ParameterExpression context)
    {
        if (property is null)
        {
            throw new InvalidOperationException($"The property '{name}' has no PropertyInfo: its code comes from the generated mapping.");
        }

        var read = Expression.Variable(typeof(TValue), "read");
        var target = Expression.Property(row, property);

        // A value in a column of its own is taken from the column right here; any other (an object, a collection, a dictionary)
        // is read by its assembler, which is called directly instead of through the delegate that sets the property.
        Expression taken;
        if (value is LeafAssembler<TValue> leaf)
        {
            var columnType = typeof(LeafColumn<TValue>);
            var column = Expression.Call(
                typeof(Unsafe), nameof(Unsafe.As), [columnType],
                Expression.Convert(Expression.ArrayIndex(Expression.Property(context, nameof(ReadContext.Columns)), Expression.Constant(leaf.FirstColumn)), typeof(object)));
            taken = Expression.Call(column, columnType.GetMethod(nameof(LeafColumn<TValue>.TryTake))!, read);
        }
        else
        {
            taken = Expression.Call(Expression.Constant(value), typeof(Assembler<TValue>).GetMethod(nameof(Assembler<TValue>.Read))!, context, read);
        }

        Expression whenNull = optional
            ? Expression.Assign(target, Expression.Default(property.PropertyType))
            : Expression.Invoke(Expression.Constant((Action<long>)(number => throw new ValueConversionException($"{description} cannot be null, but row {number} of the file has a null for it."))), Expression.Property(context, nameof(ReadContext.Row)));
        return Expression.Block(
            [read],
            Expression.IfThenElse(taken, Expression.Assign(target, Expression.Convert(read, property.PropertyType)), whenNull));
    }
}
