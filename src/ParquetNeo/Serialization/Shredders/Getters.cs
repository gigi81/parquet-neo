using System.Linq.Expressions;
using System.Reflection;

namespace ParquetNeo.Serialization.Shredders;

/// <summary>Readers of properties and of the elements of collections, of the two kinds a null is told apart in: by reference or by <see cref="Nullable{T}"/>.</summary>
internal static class Getters
{
    /// <summary>A property whose type is the type of its values: a value type that is always there, or a reference type that may be null.</summary>
    public static TryGetter<TParent, TValue> Direct<TParent, TValue>(PropertyInfo property)
    {
        var read = Compile<TParent, TValue>(property);
        return typeof(TValue).IsValueType
            ? (TParent parent, out TValue value) =>
            {
                value = read(parent);
                return true;
            }
            : (TParent parent, out TValue value) =>
            {
                value = read(parent);
                return value is not null;
            };
    }

    /// <summary>A <see cref="Nullable{T}"/> property.</summary>
    public static TryGetter<TParent, TValue> OfNullable<TParent, TValue>(PropertyInfo property)
        where TValue : struct
    {
        var read = Compile<TParent, TValue?>(property);
        return (TParent parent, out TValue value) =>
        {
            var nullable = read(parent);
            value = nullable.GetValueOrDefault();
            return nullable.HasValue;
        };
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

    /// <summary>Compiles the property getter; much faster than calling it through reflection for every row.</summary>
    private static Func<TParent, TProperty> Compile<TParent, TProperty>(PropertyInfo property)
    {
        var parent = Expression.Parameter(typeof(TParent), "parent");
        return Expression.Lambda<Func<TParent, TProperty>>(Expression.Property(parent, property), parent).Compile();
    }
}
