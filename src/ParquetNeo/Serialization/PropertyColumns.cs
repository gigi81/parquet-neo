using System.Reflection;
using ParquetNeo.ColumnTypes;

namespace ParquetNeo.Serialization;

/// <summary>Chooses the column type of a property from its .NET type, adjusted by the attributes on it.</summary>
internal static class PropertyColumns
{
    /// <summary>One kind of attribute: which settings it carries, and the words to refuse it with.</summary>
    private sealed record AttributeKind(ColumnSettingKinds Kind, Type Attribute, string Suits, string Needs);

    private static readonly AttributeKind[] Kinds =
    [
        new(ColumnSettingKinds.Decimal, typeof(ParquetDecimalAttribute), "decimal", "a decimal"),
        new(ColumnSettingKinds.Time, typeof(ParquetTimeAttribute), "TimeOnly", "a TimeOnly"),
        new(ColumnSettingKinds.Timestamp, typeof(ParquetTimestampAttribute), "DateTime and DateTimeOffset", "a DateTime or DateTimeOffset"),
    ];

    /// <summary>The column type for <typeparamref name="TValue"/>, with the settings the attributes of the property give it.</summary>
    /// <param name="lenient">
    /// Whether an attribute that does not suit the type is ignored instead of refused: the keys and values of a dictionary share
    /// the attributes of its property, which can suit just one of them.
    /// </param>
    /// <exception cref="InvalidOperationException">An attribute does not suit the type.</exception>
    public static ColumnType<TValue> For<TValue>(PropertyInfo property, bool lenient)
    {
        var type = ColumnTypeRegistry.Get<TValue>();
        var settings = new ColumnSettings();
        foreach (var kind in Kinds)
        {
            if (property.GetCustomAttribute(kind.Attribute) is not { } attribute)
            {
                continue;
            }

            if ((type.SupportedSettings & kind.Kind) == 0)
            {
                if (lenient)
                {
                    continue;
                }

                throw Misplaced(property, kind, typeof(TValue));
            }

            settings = attribute switch
            {
                ParquetDecimalAttribute decimalAttribute => settings with { Precision = decimalAttribute.Precision, Scale = decimalAttribute.Scale },
                ParquetTimeAttribute time => settings with { Unit = time.Unit, IsAdjustedToUtc = time.IsAdjustedToUtc },
                ParquetTimestampAttribute timestamp => settings with { Unit = timestamp.Unit, IsAdjustedToUtc = timestamp.IsAdjustedToUtc },
                _ => settings,
            };
        }

        return type.Configure(settings);
    }

    /// <summary>Refuses the attributes that adjust a single column on a property that is stored as several: an object or a collection of objects.</summary>
    /// <exception cref="InvalidOperationException">The property has one of them.</exception>
    public static void RejectColumnAttributes(PropertyInfo property, Type type)
    {
        foreach (var kind in Kinds)
        {
            if (property.GetCustomAttribute(kind.Attribute) is not null)
            {
                throw Misplaced(property, kind, type);
            }
        }
    }

    /// <summary>Refuses a column attribute that suits none of the types a property is stored as, such as a decimal attribute on a dictionary of text.</summary>
    /// <exception cref="InvalidOperationException">An attribute suits none of the types.</exception>
    public static void RejectUnusedAttributes(PropertyInfo property, params Type[] types)
    {
        foreach (var kind in Kinds)
        {
            if (property.GetCustomAttribute(kind.Attribute) is not null
                && !types.Any(t => ColumnTypeRegistry.TryGet(t) is { } columnType && (columnType.SupportedSettings & kind.Kind) != 0))
            {
                throw new InvalidOperationException($"{Describe(property)} has {kind.Attribute.Name}, but none of what it is made of is {kind.Needs}.");
            }
        }
    }

    public static string Describe(PropertyInfo property) => $"Property '{property.DeclaringType?.Name}.{property.Name}'";

    private static InvalidOperationException Misplaced(PropertyInfo property, AttributeKind kind, Type type) =>
        new($"{Describe(property)} has {kind.Attribute.Name}, which suits {kind.Suits}, not {type.Name}.");
}
