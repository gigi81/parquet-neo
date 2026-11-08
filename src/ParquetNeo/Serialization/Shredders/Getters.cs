using System.Linq.Expressions;
using System.Reflection;

namespace ParquetNeo.Serialization.Shredders;

/// <summary>Readers of properties and of the elements of collections, of the two kinds a null is told apart in: by reference or by <see cref="Nullable{T}"/>.</summary>
internal static class Getters
{
    /// <summary>A property whose type is the type of its values: a value type that is always there, or a reference type that may be null.</summary>
    public static TryGetter<TParent, TValue> Direct<TParent, TValue>(PropertyInfo property)
    {
        // One compiled delegate that reads the property, stores it in the out argument and says whether it is there.
        var parent = Expression.Parameter(typeof(TParent), "parent");
        var value = Expression.Parameter(typeof(TValue).MakeByRefType(), "value");
        var read = Expression.Assign(value, Expression.Property(parent, property));
        Expression present = typeof(TValue).IsValueType ? Expression.Constant(true) : Expression.NotEqual(value, Expression.Constant(null, typeof(TValue)));
        return Expression.Lambda<TryGetter<TParent, TValue>>(Expression.Block(read, present), parent, value).Compile();
    }

    /// <summary>A <see cref="Nullable{T}"/> property.</summary>
    public static TryGetter<TParent, TValue> OfNullable<TParent, TValue>(PropertyInfo property)
        where TValue : struct
    {
        var parent = Expression.Parameter(typeof(TParent), "parent");
        var value = Expression.Parameter(typeof(TValue).MakeByRefType(), "value");
        var nullable = Expression.Variable(typeof(TValue?), "nullable");
        var body = Expression.Block(
            [nullable],
            Expression.Assign(nullable, Expression.Property(parent, property)),
            Expression.Assign(value, Expression.Call(nullable, typeof(TValue?).GetMethod(nameof(Nullable<int>.GetValueOrDefault), Type.EmptyTypes)!)),
            Expression.Property(nullable, nameof(Nullable<int>.HasValue)));
        return Expression.Lambda<TryGetter<TParent, TValue>>(body, parent, value).Compile();
    }

    public static ItemReader<TValue, TValue> DirectItem<TValue>() =>
        typeof(TValue).IsValueType
            ? (TValue item, out TValue value) =>
            {
                value = item;
                return true;
            }
            : (TValue item, out TValue value) =>
            {
                value = item;
                return value is not null;
            };

    public static ItemReader<TValue?, TValue> NullableItem<TValue>()
        where TValue : struct =>
        (TValue? item, out TValue value) =>
        {
            value = item.GetValueOrDefault();
            return item.HasValue;
        };
}
