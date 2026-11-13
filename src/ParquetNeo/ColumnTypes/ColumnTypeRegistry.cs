using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

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
    /// <exception cref="NotSupportedException">The type is an enum that was not registered, in a program that cannot make types at run time (Native AOT): register it with <see cref="RegisterEnum{TEnum}"/>.</exception>
    public static ColumnType? TryGet(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (Types.TryGetValue(type, out var found))
        {
            return found;
        }

        return type.IsEnum ? Types.GetOrAdd(type, CreateEnumType(type)) : null;
    }

    /// <summary>Whether values of the type are stored in a column of their own, as opposed to nested objects and collections.</summary>
    public static bool IsSupported(Type type) => TryGet(type) is not null;

    /// <summary>Uses <paramref name="type"/> for <typeparamref name="TValue"/>, in place of any column type registered for it.</summary>
    public static void Register<TValue>(ColumnType<TValue> type)
    {
        ArgumentNullException.ThrowIfNull(type);
        Types[type.ValueType] = type;
    }

    /// <summary>
    /// Registers the column type of an enum (its values stored as their names). Enums are found without this when the program can
    /// make generic types at run time; with Native AOT it makes the enum known to the compiler, so call it once for each enum that
    /// the serializer reads or writes (the generated mapping of a type does it for the enums of the type).
    /// </summary>
    public static void RegisterEnum<TEnum>()
        where TEnum : struct, Enum => Types.TryAdd(typeof(TEnum), new EnumColumnType<TEnum>());

    [UnconditionalSuppressMessage("AOT", "IL3050:Calling members annotated with RequiresDynamicCodeAttribute may break functionality when AOT compiling.",
        Justification = "Guarded by RuntimeFeature.IsDynamicCodeSupported: where types cannot be made at run time, it explains how to register the enum instead.")]
    [UnconditionalSuppressMessage("Trimming", "IL2071:Parameter does not satisfy DynamicallyAccessedMembers requirements.",
        Justification = "EnumColumnType<> is created for an enum that is in use, so its parameterless constructor is not trimmed away; a trimmed program registers its enums with RegisterEnum.")]
    [UnconditionalSuppressMessage("Trimming", "IL2055:Call to MakeGenericType can not be statically analyzed.",
        Justification = "The arguments are enum types, which satisfy the constraint of EnumColumnType<>.")]
    private static ColumnType CreateEnumType(Type type)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            throw new NotSupportedException(
                $"The enum {type.FullName} has no column type, and this program cannot make one at run time (Native AOT). Register it once with {nameof(ColumnTypeRegistry)}.{nameof(RegisterEnum)}<{type.Name}>().");
        }

        // Enums are one family: the type for each is made the first time it is asked for.
        return (ColumnType)Activator.CreateInstance(typeof(EnumColumnType<>).MakeGenericType(type))!;
    }

    private static ConcurrentDictionary<Type, ColumnType> CreateDefaults()
    {
        ColumnType[] defaults =
        [
            new BinaryColumnType(),
            new BooleanColumnType(),
            new ByteColumnType(),
            new DateColumnType(),
            new DateTimeColumnType(),
            new DateTimeOffsetColumnType(),
            new DecimalColumnType(),
            new DoubleColumnType(),
            new FloatColumnType(),
            new GuidColumnType(),
            new HalfColumnType(),
            new Int16ColumnType(),
            new Int32ColumnType(),
            new Int64ColumnType(),
            new SByteColumnType(),
            new StringColumnType(),
            new TimeColumnType(),
            new UInt16ColumnType(),
            new UInt32ColumnType(),
            new UInt64ColumnType(),
        ];
        return new(defaults.Select(type => KeyValuePair.Create(type.ValueType, type)));
    }
}
