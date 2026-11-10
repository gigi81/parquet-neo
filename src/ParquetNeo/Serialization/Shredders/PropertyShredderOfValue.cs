using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using ParquetNeo.Schema;
using ParquetNeo.Serialization.Buffers;

namespace ParquetNeo.Serialization.Shredders;

/// <param name="property">The property, which the generated code reads.</param>
/// <param name="nullable">Whether the property is a <see cref="Nullable{T}"/> of <typeparamref name="TValue"/>.</param>
internal sealed class PropertyShredder<TValue>(string name, Shredder<TValue> value, PropertyInfo property, bool nullable)
    : PropertyShredder(name)
{
    public override SchemaNode CreateSchemaNode() => value.CreateSchemaNode(Name);

    public override Expression Emit(ParameterExpression parent, Expression definition, Expression repetition, ParameterExpression buffers)
    {
        var read = Expression.Property(parent, property);
        var alwaysThere = typeof(TValue).IsValueType && !nullable;

        // What the property is, when it can be missing: the value, and whether there is one.
        var local = Expression.Variable(property.PropertyType, "value");
        Expression present = Expression.Constant(true);
        Expression unwrapped = local;
        if (nullable)
        {
            present = Expression.Property(local, nameof(Nullable<int>.HasValue));
            unwrapped = Expression.Call(local, property.PropertyType.GetMethod(nameof(Nullable<int>.GetValueOrDefault), Type.EmptyTypes)!);
        }
        else if (!alwaysThere)
        {
            present = Expression.NotEqual(local, Expression.Constant(null, property.PropertyType));
        }

        if (value is LeafShredder<TValue> leaf)
        {
            // A single column: the value goes straight to its buffer.
            var bufferType = typeof(LeafBuffer<TValue>);
            Expression Buffer() => Expression.Call(
                typeof(Unsafe), nameof(Unsafe.As), [bufferType], Expression.Convert(Expression.ArrayIndex(buffers, Expression.Constant(leaf.FirstColumn)), typeof(object)));

            var add = bufferType.GetMethod(nameof(LeafBuffer<TValue>.AddValue))!;
            var addMissing = bufferType.GetMethod(nameof(LeafBuffer<TValue>.AddMissing))!;
            Expression valueDefinition = leaf.Optional ? Expression.Add(definition, Expression.Constant(1)) : definition;
            if (alwaysThere)
            {
                return Expression.Call(Buffer(), add, read, valueDefinition, repetition);
            }

            Expression whenMissing = leaf.Optional
                ? Expression.Call(Buffer(), addMissing, definition, repetition)
                : Expression.Invoke(Expression.Constant((Action)(() => throw leaf.Missing())));
            return Expression.Block(
                [local],
                Expression.Assign(local, read),
                Expression.IfThenElse(present, Expression.Call(Buffer(), add, unwrapped, valueDefinition, repetition), whenMissing));
        }

        // An object, a collection or a dictionary: its shredder does the rest.
        var shred = typeof(Shredder<TValue>).GetMethod(nameof(Shredder<TValue>.Shred))!;
        var target = Expression.Constant(value);
        if (alwaysThere)
        {
            return Expression.Call(target, shred, read, Expression.Constant(true), definition, repetition, buffers);
        }

        return Expression.Block(
            [local],
            Expression.Assign(local, read),
            Expression.Call(target, shred, unwrapped, present, definition, repetition, buffers));
    }
}
