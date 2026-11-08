using ParquetNeo.Exceptions;

namespace ParquetNeo.Serialization.Assemblers;

/// <summary>Finds the value of an enum from its name, looked up in a table instead of parsed for every row.</summary>
internal static class EnumParser<TEnum> where TEnum : struct, Enum
{
    private static readonly Dictionary<string, TEnum> Values = Create();

    /// <summary>Names are case sensitive. What <see cref="Enum.ToString()"/> gives for a combination of flags or a number is understood too.</summary>
    /// <exception cref="ValueConversionException">The text is not the name of a value.</exception>
    public static TEnum Parse(string name)
    {
        if (Values.TryGetValue(name, out var value))
        {
            return value;
        }

        if (Enum.TryParse(name, ignoreCase: false, out value))
        {
            return value;
        }

        throw new ValueConversionException($"'{name}' is not the name of a value of the enum {typeof(TEnum).Name}.");
    }

    private static Dictionary<string, TEnum> Create()
    {
        var values = new Dictionary<string, TEnum>(StringComparer.Ordinal);
        foreach (var value in Enum.GetValues<TEnum>())
        {
            values.TryAdd(value.ToString(), value);
        }

        return values;
    }
}
