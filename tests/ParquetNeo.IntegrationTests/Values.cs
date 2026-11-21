using System.Globalization;
using System.Text.Json;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.IntegrationTests;

/// <summary>
/// Compares what Python says a column holds (JSON, normalized as in <c>normalize.py</c>) with what ParquetNeo holds (a .NET value), in
/// either direction of the tests. A comparison that fails gives text that says where; one that holds gives null.
/// </summary>
internal static class Values
{
    private static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    /// <summary>Whether the .NET value is the JSON value of <paramref name="column"/>, which says the units of times and the scale of decimals.</summary>
    public static string? Compare(JsonElement python, object? value, PrimitiveNode column)
    {
        if (value is DBNull)
        {
            value = null;
        }

        if (python.ValueKind == JsonValueKind.Null || value is null)
        {
            return python.ValueKind == JsonValueKind.Null && value is null ? null : $"expected {python.GetRawText()}, found {Describe(value)}";
        }

        bool same = value switch
        {
            bool flag => python.ValueKind == (flag ? JsonValueKind.True : JsonValueKind.False),
            sbyte or byte or short or ushort or int or long => python.ValueKind == JsonValueKind.Number && python.TryGetInt64(out long l) && l == Convert.ToInt64(value, CultureInfo.InvariantCulture),
            uint or ulong => python.ValueKind == JsonValueKind.Number && python.TryGetUInt64(out ulong u) && u == Convert.ToUInt64(value, CultureInfo.InvariantCulture),
            float single => SameFloat(python, single),
            double number => SameFloat(python, number),
            Half half => SameFloat(python, (double)half),
            decimal number => python.ValueKind == JsonValueKind.String && python.GetString() == number.ToString("F" + (column.LogicalType?.Scale ?? 0), CultureInfo.InvariantCulture),
            string text => python.ValueKind == JsonValueKind.String && python.GetString() == text,
            byte[] bytes => python.ValueKind == JsonValueKind.String && python.GetString() == Convert.ToHexStringLower(bytes),
            Guid guid => python.ValueKind == JsonValueKind.String && python.GetString() == guid.ToString("N"),
            DateOnly date => IsInteger(python, date.DayNumber - DateOnly.FromDateTime(Epoch).DayNumber),
            TimeOnly time => IsInteger(python, time.Ticks * 100 / NanosecondsPerUnit(column)),
            DateTime moment => IsInteger(python, (moment.Ticks - Epoch.Ticks) * 100 / NanosecondsPerUnit(column)),
            DateTimeOffset moment => IsInteger(python, (moment.UtcTicks - Epoch.Ticks) * 100 / NanosecondsPerUnit(column)),
            _ => throw new NotSupportedException($"The test cannot compare a {value.GetType().Name}."),
        };
        return same ? null : $"expected {python.GetRawText()}, found {Describe(value)}";
    }

    /// <summary>Whether two JSON values are the same: numbers by their value, objects by their properties, whatever the order.</summary>
    public static string? Compare(JsonElement expected, JsonElement actual, string path = "$")
    {
        if (expected.ValueKind != actual.ValueKind && !(expected.ValueKind is JsonValueKind.True or JsonValueKind.False && actual.ValueKind is JsonValueKind.True or JsonValueKind.False))
        {
            return $"{path}: expected {expected.GetRawText()}, found {actual.GetRawText()}";
        }

        switch (expected.ValueKind)
        {
            case JsonValueKind.Number:
                bool equal = expected.TryGetInt64(out long a) && actual.TryGetInt64(out long b) ? a == b : expected.GetDouble().Equals(actual.GetDouble());
                return equal ? null : $"{path}: expected {expected.GetRawText()}, found {actual.GetRawText()}";
            case JsonValueKind.String:
                return expected.GetString() == actual.GetString() ? null : $"{path}: expected {expected.GetRawText()}, found {actual.GetRawText()}";
            case JsonValueKind.True:
            case JsonValueKind.False:
                return expected.GetBoolean() == actual.GetBoolean() ? null : $"{path}: expected {expected.GetRawText()}, found {actual.GetRawText()}";
            case JsonValueKind.Array:
                return CompareArrays(expected, actual, path);
            case JsonValueKind.Object:
                return CompareObjects(expected, actual, path);
            default:
                return null;
        }
    }

    private static string? CompareArrays(JsonElement expected, JsonElement actual, string path)
    {
        if (expected.GetArrayLength() != actual.GetArrayLength())
        {
            return $"{path}: expected {expected.GetArrayLength()} items, found {actual.GetArrayLength()}";
        }

        int index = 0;
        foreach (var (left, right) in expected.EnumerateArray().Zip(actual.EnumerateArray()))
        {
            if (Compare(left, right, $"{path}[{index++}]") is { } difference)
            {
                return difference;
            }
        }

        return null;
    }

    private static string? CompareObjects(JsonElement expected, JsonElement actual, string path)
    {
        var names = expected.EnumerateObject().Select(p => p.Name).ToHashSet();
        if (!names.SetEquals(actual.EnumerateObject().Select(p => p.Name)))
        {
            return $"{path}: expected the properties {string.Join(",", names.Order())}, found {string.Join(",", actual.EnumerateObject().Select(p => p.Name).Order())}";
        }

        foreach (string name in names)
        {
            if (Compare(expected.GetProperty(name), actual.GetProperty(name), $"{path}.{name}") is { } difference)
            {
                return difference;
            }
        }

        return null;
    }

    private static bool SameFloat(JsonElement python, double value) => python.ValueKind switch
    {
        JsonValueKind.Number => !double.IsNaN(value) && !double.IsInfinity(value) && python.GetDouble().Equals(value),
        JsonValueKind.String => python.GetString() switch
        {
            "NaN" => double.IsNaN(value),
            "Infinity" => double.IsPositiveInfinity(value),
            "-Infinity" => double.IsNegativeInfinity(value),
            _ => false,
        },
        _ => false,
    };

    private static bool IsInteger(JsonElement python, long expected) => python.ValueKind == JsonValueKind.Number && python.TryGetInt64(out long value) && value == expected;

    private static long NanosecondsPerUnit(PrimitiveNode column) => column.LogicalType?.Unit switch
    {
        TimeUnit.Millis => 1_000_000,
        TimeUnit.Micros => 1_000,
        TimeUnit.Nanos => 1,
        _ => throw new InvalidOperationException($"Column '{column.Name}' has no time unit."),
    };

    private static string Describe(object? value) => value switch
    {
        null => "null",
        byte[] bytes => Convert.ToHexStringLower(bytes),
        string text => "\"" + text + "\"",
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture) + " (" + value.GetType().Name + ")",
        _ => value.ToString() + " (" + value.GetType().Name + ")",
    };
}
