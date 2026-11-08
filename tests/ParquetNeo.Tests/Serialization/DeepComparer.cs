using System.Collections;
using System.Reflection;

namespace ParquetNeo.Tests.Serialization;

/// <summary>Compares object graphs by value: properties, sequences in order, dictionaries by key, byte arrays by content.</summary>
internal static class DeepComparer
{
    /// <summary>The first difference between two values, or null when they are equal.</summary>
    public static string? Difference(object? expected, object? actual, string path = "")
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null ? null : $"{Where(path)}: expected {Show(expected)}, got {Show(actual)}";
        }

        var type = expected.GetType();
        if (type != actual.GetType() && !(expected is IEnumerable && actual is IEnumerable))
        {
            return $"{Where(path)}: expected a {type.Name}, got a {actual.GetType().Name}";
        }

        if (expected is byte[] bytes)
        {
            return actual is byte[] other && bytes.AsSpan().SequenceEqual(other) ? null : $"{Where(path)}: expected {Show(expected)}, got {Show(actual)}";
        }

        if (expected is string || type.IsPrimitive || type.IsEnum || type == typeof(decimal) || type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset)
            || type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(Half))
        {
            return Equals(expected, actual) ? null : $"{Where(path)}: expected {Show(expected)}, got {Show(actual)}";
        }

        if (expected is IDictionary dictionary)
        {
            var actualDictionary = (IDictionary)actual;
            if (dictionary.Count != actualDictionary.Count)
            {
                return $"{Where(path)}: expected {dictionary.Count} entries, got {actualDictionary.Count}";
            }

            foreach (DictionaryEntry entry in dictionary)
            {
                if (!actualDictionary.Contains(entry.Key))
                {
                    return $"{Where(path)}: key {Show(entry.Key)} is missing";
                }

                if (Difference(entry.Value, actualDictionary[entry.Key], $"{path}[{Show(entry.Key)}]") is { } difference)
                {
                    return difference;
                }
            }

            return null;
        }

        if (expected is IEnumerable sequence)
        {
            var left = sequence.Cast<object?>().ToList();
            var right = ((IEnumerable)actual).Cast<object?>().ToList();
            if (left.Count != right.Count)
            {
                return $"{Where(path)}: expected {left.Count} elements, got {right.Count}";
            }

            for (int i = 0; i < left.Count; i++)
            {
                if (Difference(left[i], right[i], $"{path}[{i}]") is { } difference)
                {
                    return difference;
                }
            }

            return null;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0))
        {
            if (Difference(property.GetValue(expected), property.GetValue(actual), path.Length == 0 ? property.Name : $"{path}.{property.Name}") is { } difference)
            {
                return difference;
            }
        }

        return null;
    }

    private static string Where(string path) => path.Length == 0 ? "the value" : path;

    private static string Show(object? value) => value switch
    {
        null => "null",
        byte[] bytes => Convert.ToHexString(bytes),
        string text => $"\"{text}\"",
        _ => value.ToString() ?? "",
    };
}
