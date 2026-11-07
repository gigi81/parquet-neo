using System.Linq.Expressions;
using System.Reflection;
using ParquetNeo.Metadata;

namespace ParquetNeo.Serialization;

/// <summary>
/// The columns a row type is written as, found once by reflection from its public properties and cached. A property is a
/// column unless it has <see cref="ParquetIgnoreAttribute"/>.
/// </summary>
internal static class RowModel<TRow>
{
    private static IReadOnlyList<ColumnDefinition<TRow>>? _columns;

    /// <exception cref="NotSupportedException">A property has a type that cannot be written as a column.</exception>
    /// <exception cref="InvalidOperationException">The type has no columns, two properties share a column name, or an attribute does not suit its property.</exception>
    public static IReadOnlyList<ColumnDefinition<TRow>> Columns => _columns ??= Build();

    private static List<ColumnDefinition<TRow>> Build()
    {
        var properties = typeof(TRow).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0 && p.GetCustomAttribute<ParquetIgnoreAttribute>() is null)
            .OrderBy(p => p.GetCustomAttribute<ParquetColumnAttribute>()?.Order ?? 0)
            .ThenBy(p => Depth(p.DeclaringType!))
            .ThenBy(p => p.MetadataToken)
            .ToList();

        var columns = new List<ColumnDefinition<TRow>>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            var column = Create(property);
            if (!names.Add(column.Name))
            {
                throw new InvalidOperationException($"Two properties of {typeof(TRow).Name} are written to a column named '{column.Name}'. Rename one with {nameof(ParquetColumnAttribute)} or leave it out with {nameof(ParquetIgnoreAttribute)}.");
            }

            columns.Add(column);
        }

        if (columns.Count == 0)
        {
            throw new InvalidOperationException($"{typeof(TRow).Name} has no public properties to write as columns.");
        }

        return columns;
    }

    private static ColumnDefinition<TRow> Create(PropertyInfo property)
    {
        var nullable = Nullable.GetUnderlyingType(property.PropertyType);
        var valueType = nullable ?? property.PropertyType;
        var shape = ColumnShapes.For(valueType, property);
        string name = property.GetCustomAttribute<ParquetColumnAttribute>()?.Name ?? property.Name;
        var encoding = property.GetCustomAttribute<ParquetEncodingAttribute>()?.Encoding;

        var factory = typeof(RowModel<TRow>).GetMethod(nullable is not null ? nameof(CreateNullable) : nameof(CreateDirect), BindingFlags.NonPublic | BindingFlags.Static)!;
        bool optional = nullable is not null || (!valueType.IsValueType && IsNullableReference(property));
        return (ColumnDefinition<TRow>)factory.MakeGenericMethod(valueType).Invoke(null, [property, name, optional, shape, encoding])!;
    }

    /// <summary>A property whose type is the type of its values: a value type that is always there, or a reference type that may be null.</summary>
    private static ColumnDefinition<TRow> CreateDirect<TValue>(PropertyInfo property, string name, bool optional, ColumnShape shape, ParquetEncoding? encoding)
    {
        var read = Getter<TValue>(property);
        TryGetter<TRow, TValue> getter = typeof(TValue).IsValueType
            ? (TRow row, out TValue value) =>
            {
                value = read(row);
                return true;
            }
            : (TRow row, out TValue value) =>
            {
                value = read(row);
                return value is not null;
            };
        return new TypedColumnDefinition<TRow, TValue>(name, ColumnShapes.Describe(property), optional, shape, getter, ColumnWriters.For<TValue>(), encoding);
    }

    /// <summary>A <see cref="Nullable{T}"/> property: the column is optional, and a null is a row without a value.</summary>
    private static ColumnDefinition<TRow> CreateNullable<TValue>(PropertyInfo property, string name, bool optional, ColumnShape shape, ParquetEncoding? encoding)
        where TValue : struct
    {
        var read = Getter<TValue?>(property);
        TryGetter<TRow, TValue> getter = (TRow row, out TValue value) =>
        {
            var nullable = read(row);
            value = nullable.GetValueOrDefault();
            return nullable.HasValue;
        };
        return new TypedColumnDefinition<TRow, TValue>(name, ColumnShapes.Describe(property), optional, shape, getter, ColumnWriters.For<TValue>(), encoding);
    }

    /// <summary>Compiles the property getter; much faster than calling it through reflection for every row.</summary>
    private static Func<TRow, TProperty> Getter<TProperty>(PropertyInfo property)
    {
        var row = Expression.Parameter(typeof(TRow), "row");
        return Expression.Lambda<Func<TRow, TProperty>>(Expression.Property(row, property), row).Compile();
    }

    /// <summary>A reference type is optional unless the code says it never holds null (nullable reference types).</summary>
    private static bool IsNullableReference(PropertyInfo property) =>
        new NullabilityInfoContext().Create(property).ReadState != NullabilityState.NotNull;

    private static int Depth(Type type)
    {
        int depth = 0;
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            depth++;
        }

        return depth;
    }
}
