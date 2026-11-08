namespace ParquetNeo.Serialization.Shredders;

/// <summary>The names of the values of an enum, looked up in a table instead of formatted for every row.</summary>
internal static class EnumNames<TEnum> where TEnum : struct, Enum
{
    private static readonly Dictionary<TEnum, string> Names = Create();

    /// <summary>The name of the value; a combination of flags, which has no entry, is formatted as <see cref="Enum.ToString()"/> does.</summary>
    public static string Of(TEnum value) => Names.TryGetValue(value, out string? name) ? name : value.ToString();

    private static Dictionary<TEnum, string> Create()
    {
        var names = new Dictionary<TEnum, string>();
        foreach (var value in Enum.GetValues<TEnum>())
        {
            names.TryAdd(value, value.ToString());
        }

        return names;
    }
}
