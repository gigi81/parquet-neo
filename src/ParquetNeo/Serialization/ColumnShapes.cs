using System.Reflection;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Serialization;

/// <summary>The convention that decides how the type of a property is stored, adjusted by the attributes on it.</summary>
internal static class ColumnShapes
{
    private const int DefaultDecimalPrecision = 38;
    private const int DefaultDecimalScale = 18;

    /// <exception cref="NotSupportedException">The type cannot be written as a column.</exception>
    /// <exception cref="InvalidOperationException">An attribute does not suit the type.</exception>
    public static ColumnShape For(Type type, PropertyInfo property)
    {
        var decimalAttribute = property.GetCustomAttribute<ParquetDecimalAttribute>();
        var timestampAttribute = property.GetCustomAttribute<ParquetTimestampAttribute>();
        var timeAttribute = property.GetCustomAttribute<ParquetTimeAttribute>();
        RejectMisplaced(type, property, decimalAttribute is not null, typeof(decimal), nameof(ParquetDecimalAttribute));
        RejectMisplaced(type, property, timeAttribute is not null, typeof(TimeOnly), nameof(ParquetTimeAttribute));
        if (timestampAttribute is not null && type != typeof(DateTime) && type != typeof(DateTimeOffset))
        {
            throw new InvalidOperationException($"{Describe(property)} has {nameof(ParquetTimestampAttribute)}, which suits DateTime and DateTimeOffset, not {type.Name}.");
        }

        if (type == typeof(bool))
        {
            return new(PhysicalType.Boolean, null);
        }

        if (type == typeof(sbyte))
        {
            return new(PhysicalType.Int32, LogicalType.Integer(8, true));
        }

        if (type == typeof(byte))
        {
            return new(PhysicalType.Int32, LogicalType.Integer(8, false));
        }

        if (type == typeof(short))
        {
            return new(PhysicalType.Int32, LogicalType.Integer(16, true));
        }

        if (type == typeof(ushort))
        {
            return new(PhysicalType.Int32, LogicalType.Integer(16, false));
        }

        if (type == typeof(int))
        {
            return new(PhysicalType.Int32, null);
        }

        if (type == typeof(uint))
        {
            return new(PhysicalType.Int32, LogicalType.Integer(32, false));
        }

        if (type == typeof(long))
        {
            return new(PhysicalType.Int64, null);
        }

        if (type == typeof(ulong))
        {
            return new(PhysicalType.Int64, LogicalType.Integer(64, false));
        }

        if (type == typeof(float))
        {
            return new(PhysicalType.Float, null);
        }

        if (type == typeof(double))
        {
            return new(PhysicalType.Double, null);
        }

        if (type == typeof(Half))
        {
            return new(PhysicalType.FixedLenByteArray, LogicalType.Float16, 2);
        }

        if (type == typeof(decimal))
        {
            return Decimal(decimalAttribute?.Precision ?? DefaultDecimalPrecision, decimalAttribute?.Scale ?? DefaultDecimalScale);
        }

        if (type == typeof(string))
        {
            return new(PhysicalType.ByteArray, LogicalType.String);
        }

        if (type == typeof(byte[]))
        {
            return new(PhysicalType.ByteArray, null);
        }

        if (type == typeof(Guid))
        {
            return new(PhysicalType.FixedLenByteArray, LogicalType.Uuid, 16);
        }

        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
        {
            return new(PhysicalType.Int64, LogicalType.Timestamp(timestampAttribute?.IsAdjustedToUtc ?? true, timestampAttribute?.Unit ?? TimeUnit.Micros));
        }

        if (type == typeof(DateOnly))
        {
            return new(PhysicalType.Int32, LogicalType.Date);
        }

        if (type == typeof(TimeOnly))
        {
            var unit = timeAttribute?.Unit ?? TimeUnit.Micros;
            return new(unit == TimeUnit.Millis ? PhysicalType.Int32 : PhysicalType.Int64, LogicalType.Time(timeAttribute?.IsAdjustedToUtc ?? false, unit));
        }

        throw new NotSupportedException($"{Describe(property)} is a {type}, which cannot be written as a column.");
    }

    private static ColumnShape Decimal(int precision, int scale)
    {
        var logical = LogicalType.Decimal(scale, precision);
        if (precision <= 9)
        {
            return new(PhysicalType.Int32, logical);
        }

        if (precision <= 18)
        {
            return new(PhysicalType.Int64, logical);
        }

        int length = 1;
        while (LogicalTypeRules.MaxDecimalPrecision(length) < precision)
        {
            length++;
        }

        return new(PhysicalType.FixedLenByteArray, logical, length);
    }

    private static void RejectMisplaced(Type type, PropertyInfo property, bool hasAttribute, Type expected, string attribute)
    {
        if (hasAttribute && type != expected)
        {
            throw new InvalidOperationException($"{Describe(property)} has {attribute}, which suits {expected.Name}, not {type.Name}.");
        }
    }

    public static string Describe(PropertyInfo property) => $"Property '{property.DeclaringType?.Name}.{property.Name}'";
}
