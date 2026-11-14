using System.Reflection;

namespace ParquetNeo.Serialization.Tests;

/// <summary>Compares rows property by property, byte arrays by content.</summary>
internal static class RowComparer
{
    /// <summary>The first difference between two rows, or null when they are equal.</summary>
    public static string? Difference<T>(T expected, T actual)
    {
        foreach (var property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0))
        {
            object? want = property.GetValue(expected);
            object? got = property.GetValue(actual);
            bool same = want is byte[] wantBytes && got is byte[] gotBytes ? wantBytes.AsSpan().SequenceEqual(gotBytes) : Equals(want, got);
            if (!same)
            {
                return $"{property.Name}: expected {Show(want)}, got {Show(got)}";
            }
        }

        return null;
    }

    private static string Show(object? value) => value switch
    {
        null => "null",
        byte[] bytes => Convert.ToHexString(bytes),
        _ => value.ToString() ?? "",
    };
}
