using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using ParquetNeo.Schema;
using ParquetNeo.Serialization.Buffers;

namespace ParquetNeo.Serialization.Shredders;

/// <param name="property">The property, for the compiled form of the shredding.</param>
/// <param name="nullable">Whether the property is a <see cref="Nullable{T}"/> of <typeparamref name="TValue"/>.</param>
internal sealed class PropertyShredder<TParent, TValue>(string name, TryGetter<TParent, TValue> getter, Shredder<TValue> value, PropertyInfo property, bool nullable)
    : PropertyShredder<TParent>(name)
{
    public override SchemaNode CreateSchemaNode() => value.CreateSchemaNode(Name);

    public override void Shred(TParent parent, int definition, int repetition, ILeafBuffer[] buffers)
    {
        bool present = getter(parent, out TValue? read);
        value.Shred(read, present, definition, repetition, buffers);
    }

    public override Expression? EmitFlat(ParameterExpression parent, ParameterExpression buffers)
    {
        if (value is not LeafShredder<TValue> leaf)
        {
            return null;
        }

        // The buffer of the column, as its own type: the array holds exactly those.
        var bufferType = typeof(LeafBuffer<TValue>);
        Expression Buffer() => Expression.Call(
            typeof(Unsafe), nameof(Unsafe.As), [bufferType], Expression.Convert(Expression.ArrayIndex(buffers, Expression.Constant(leaf.FirstColumn)), typeof(object)));

        var add = bufferType.GetMethod(nameof(LeafBuffer<TValue>.AddValue))!;
        var addMissing = bufferType.GetMethod(nameof(LeafBuffer<TValue>.AddMissing))!;
        var read = Expression.Property(parent, property);
        var definition = Expression.Constant(leaf.Optional ? 1 : 0);
        var repetition = Expression.Constant(0);
        if (typeof(TValue).IsValueType && !nullable)
        {
            // A value that is always there.
            return Expression.Call(Buffer(), add, read, definition, repetition);
        }

        Expression whenMissing = leaf.Optional
            ? Expression.Call(Buffer(), addMissing, Expression.Constant(0), repetition)
            : Expression.Invoke(Expression.Constant((Action)(() => throw leaf.Missing())));
        var local = Expression.Variable(property.PropertyType, "value");
        Expression present = nullable
            ? Expression.Property(local, nameof(Nullable<int>.HasValue))
            : Expression.NotEqual(local, Expression.Constant(null, property.PropertyType));
        Expression unwrapped = nullable
            ? Expression.Call(local, property.PropertyType.GetMethod(nameof(Nullable<int>.GetValueOrDefault), Type.EmptyTypes)!)
            : local;
        return Expression.Block(
            [local],
            Expression.Assign(local, read),
            Expression.IfThenElse(present, Expression.Call(Buffer(), add, unwrapped, definition, repetition), whenMissing));
    }
}
