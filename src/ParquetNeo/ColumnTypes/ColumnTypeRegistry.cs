using System.Collections.Concurrent;

namespace ParquetNeo.ColumnTypes;

/// <summary>
/// Finds the <see cref="ColumnType{TValue}"/> that reads and writes values of a .NET type: the built-in ones, any enum (as the
/// name of its value), and the ones added with <see cref="Register{TValue}"/>. The serializer uses it to decide how a property is
/// stored, so registering a column type teaches it a new type for its properties.
/// </summary>
public static class ColumnTypeRegistry
{
    private static readonly ConcurrentDictionary<Type, ColumnType> Types = CreateDefaults();

    /// <summary>The column type for <typeparamref name="TValue"/>, with its default settings.</summary>
    /// <exception cref="NotSupportedException">No column type is registered for the type, and it is not an enum.</exception>
    public static ColumnType<TValue> Get<TValue>() =>
        TryGet<TValue>() ?? throw new NotSupportedException($"{typeof(TValue)} has no column type; add one with {nameof(ColumnTypeRegistry)}.{nameof(Register)}.");

    /// <summary>The column type for <typeparamref name="TValue"/>, or null when there is none.</summary>
    public static ColumnType<TValue>? TryGet<TValue>() => (ColumnType<TValue>?)TryGet(typeof(TValue));

    /// <summary>The column type for a .NET type, or null when there is none.</summary>
    public static ColumnType? TryGet(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (Types.TryGetValue(type, out var found))
        {
            return found;
        }

        if (type.IsEnum)
        {
            // Enums are one family: the type for each is made the first time it is asked for.
            var made = (ColumnType)Activator.CreateInstance(typeof(EnumColumnType<>).MakeGenericType(type))!;
            return Types.GetOrAdd(type, made);
        }

        return null;
    }

    /// <summary>Whether values of the type are stored in a column of their own, as opposed to nested objects and collections.</summary>
    public static bool IsSupported(Type type) => TryGet(type) is not null;

    /// <summary>Uses <paramref name="type"/> for <typeparamref name="TValue"/>, in place of any column type registered for it.</summary>
    public static void Register<TValue>(ColumnType<TValue> type)
    {
        ArgumentNullException.ThrowIfNull(type);
        Types[type.ValueType] = type;
    }

    private static ConcurrentDictionary<Type, ColumnType> CreateDefaults()
    {
        ColumnType[] defaults =
        [
            new BooleanColumnType(), new SByteColumnType(), new ByteColumnType(), new Int16ColumnType(), new UInt16ColumnType(),
            new Int32ColumnType(), new UInt32ColumnType(), new Int64ColumnType(), new UInt64ColumnType(), new FloatColumnType(),
            new DoubleColumnType(), new HalfColumnType(), new DecimalColumnType(), new StringColumnType(), new BinaryColumnType(),
            new GuidColumnType(), new DateTimeColumnType(), new DateTimeOffsetColumnType(), new DateColumnType(), new TimeColumnType(),
        ];
        return new(defaults.Select(type => KeyValuePair.Create(type.ValueType, type)));
    }
}
