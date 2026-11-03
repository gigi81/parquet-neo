using ParquetNeo.Metadata;

namespace ParquetNeo.Schema;

/// <summary>
/// The spec's rules tying logical types to physical types, and converting between the current
/// <see cref="LogicalType"/> and the legacy <see cref="ConvertedType"/> annotations.
/// </summary>
internal static class LogicalTypeRules
{
    /// <summary>The logical type implied by a legacy annotation, or null when there is none.</summary>
    public static LogicalType? FromConvertedType(ConvertedType? converted, int? scale, int? precision) => converted switch
    {
        ConvertedType.Utf8 => LogicalType.String,
        ConvertedType.Map => LogicalType.Map,
        ConvertedType.List => LogicalType.List,
        ConvertedType.Enum => LogicalType.Enum,
        ConvertedType.Decimal when precision is not null => LogicalType.Decimal(scale ?? 0, precision.Value),
        ConvertedType.Date => LogicalType.Date,
        // The legacy time and timestamp annotations always mean "adjusted to UTC".
        ConvertedType.TimeMillis => LogicalType.Time(isAdjustedToUtc: true, TimeUnit.Millis),
        ConvertedType.TimeMicros => LogicalType.Time(isAdjustedToUtc: true, TimeUnit.Micros),
        ConvertedType.TimestampMillis => LogicalType.Timestamp(isAdjustedToUtc: true, TimeUnit.Millis),
        ConvertedType.TimestampMicros => LogicalType.Timestamp(isAdjustedToUtc: true, TimeUnit.Micros),
        ConvertedType.UInt8 => LogicalType.Integer(8, isSigned: false),
        ConvertedType.UInt16 => LogicalType.Integer(16, isSigned: false),
        ConvertedType.UInt32 => LogicalType.Integer(32, isSigned: false),
        ConvertedType.UInt64 => LogicalType.Integer(64, isSigned: false),
        ConvertedType.Int8 => LogicalType.Integer(8, isSigned: true),
        ConvertedType.Int16 => LogicalType.Integer(16, isSigned: true),
        ConvertedType.Int32 => LogicalType.Integer(32, isSigned: true),
        ConvertedType.Int64 => LogicalType.Integer(64, isSigned: true),
        ConvertedType.Json => LogicalType.Json,
        ConvertedType.Bson => LogicalType.Bson,
        // MAP_KEY_VALUE and INTERVAL have no logical type counterpart.
        _ => null,
    };

    /// <summary>
    /// The legacy annotation writers must emit alongside a logical type. Local (not UTC-adjusted)
    /// times and timestamps get the same legacy annotation as adjusted ones, as the spec requires.
    /// </summary>
    public static ConvertedType? ToConvertedType(LogicalType? logicalType) => logicalType?.Kind switch
    {
        LogicalTypeKind.String => ConvertedType.Utf8,
        LogicalTypeKind.Map => ConvertedType.Map,
        LogicalTypeKind.List => ConvertedType.List,
        LogicalTypeKind.Enum => ConvertedType.Enum,
        LogicalTypeKind.Decimal => ConvertedType.Decimal,
        LogicalTypeKind.Date => ConvertedType.Date,
        LogicalTypeKind.Time => logicalType.Unit switch
        {
            TimeUnit.Millis => ConvertedType.TimeMillis,
            TimeUnit.Micros => ConvertedType.TimeMicros,
            _ => null,
        },
        LogicalTypeKind.Timestamp => logicalType.Unit switch
        {
            TimeUnit.Millis => ConvertedType.TimestampMillis,
            TimeUnit.Micros => ConvertedType.TimestampMicros,
            _ => null,
        },
        LogicalTypeKind.Integer => (logicalType.BitWidth, logicalType.IsSigned) switch
        {
            (8, true) => ConvertedType.Int8,
            (16, true) => ConvertedType.Int16,
            (32, true) => ConvertedType.Int32,
            (64, true) => ConvertedType.Int64,
            (8, false) => ConvertedType.UInt8,
            (16, false) => ConvertedType.UInt16,
            (32, false) => ConvertedType.UInt32,
            (64, false) => ConvertedType.UInt64,
            _ => null,
        },
        LogicalTypeKind.Json => ConvertedType.Json,
        LogicalTypeKind.Bson => ConvertedType.Bson,
        _ => null, // UUID, FLOAT16 and UNKNOWN have no legacy annotation
    };

    /// <summary>Returns why <paramref name="logicalType"/> cannot annotate the node, or null when it can.</summary>
    public static string? GetError(LogicalType logicalType, bool isGroup, PhysicalType physicalType, int? typeLength)
    {
        if (isGroup)
        {
            return logicalType.Kind is LogicalTypeKind.List or LogicalTypeKind.Map
                ? null
                : $"{logicalType.Kind} cannot annotate a group.";
        }

        switch (logicalType.Kind)
        {
            case LogicalTypeKind.String:
            case LogicalTypeKind.Enum:
            case LogicalTypeKind.Json:
            case LogicalTypeKind.Bson:
                return physicalType == PhysicalType.ByteArray ? null : $"{logicalType.Kind} requires BYTE_ARRAY.";
            case LogicalTypeKind.Uuid:
                return physicalType == PhysicalType.FixedLenByteArray && typeLength == 16
                    ? null
                    : "UUID requires FIXED_LEN_BYTE_ARRAY(16).";
            case LogicalTypeKind.Float16:
                return physicalType == PhysicalType.FixedLenByteArray && typeLength == 2
                    ? null
                    : "FLOAT16 requires FIXED_LEN_BYTE_ARRAY(2).";
            case LogicalTypeKind.Date:
                return physicalType == PhysicalType.Int32 ? null : "DATE requires INT32.";
            case LogicalTypeKind.Time:
                return logicalType.Unit switch
                {
                    TimeUnit.Millis when physicalType == PhysicalType.Int32 => null,
                    TimeUnit.Micros or TimeUnit.Nanos when physicalType == PhysicalType.Int64 => null,
                    TimeUnit.Millis => "TIME(MILLIS) requires INT32.",
                    TimeUnit.Micros or TimeUnit.Nanos => "TIME(MICROS/NANOS) requires INT64.",
                    _ => "TIME has an unknown unit.",
                };
            case LogicalTypeKind.Timestamp:
                if (!Enum.IsDefined(logicalType.Unit))
                {
                    return "TIMESTAMP has an unknown unit.";
                }

                // Out-of-int64-range nanosecond timestamps may use FIXED_LEN_BYTE_ARRAY(12).
                bool wideNanos = physicalType == PhysicalType.FixedLenByteArray && typeLength == 12
                                 && logicalType.Unit == TimeUnit.Nanos;
                return physicalType == PhysicalType.Int64 || wideNanos ? null : "TIMESTAMP requires INT64.";
            case LogicalTypeKind.Integer:
                return logicalType.BitWidth switch
                {
                    8 or 16 or 32 when physicalType == PhysicalType.Int32 => null,
                    64 when physicalType == PhysicalType.Int64 => null,
                    8 or 16 or 32 => $"INT({logicalType.BitWidth}) requires INT32.",
                    64 => "INT(64) requires INT64.",
                    _ => $"INT({logicalType.BitWidth}) is not a valid bit width.",
                };
            case LogicalTypeKind.Decimal:
                return GetDecimalError(logicalType, physicalType, typeLength);
            case LogicalTypeKind.Null:
                return physicalType == PhysicalType.Int96 ? "UNKNOWN cannot annotate INT96." : null;
            default:
                return $"{logicalType.Kind} cannot annotate a primitive column.";
        }
    }

    private static string? GetDecimalError(LogicalType logicalType, PhysicalType physicalType, int? typeLength)
    {
        int precision = logicalType.Precision;
        if (precision < 1)
        {
            return "DECIMAL precision must be positive.";
        }

        if (logicalType.Scale < 0 || logicalType.Scale > precision)
        {
            return "DECIMAL scale must be between 0 and the precision.";
        }

        return physicalType switch
        {
            PhysicalType.Int32 => precision <= 9 ? null : "DECIMAL on INT32 allows precision up to 9.",
            PhysicalType.Int64 => precision <= 18 ? null : "DECIMAL on INT64 allows precision up to 18.",
            PhysicalType.ByteArray => null,
            PhysicalType.FixedLenByteArray when typeLength is > 0 =>
                precision <= MaxDecimalPrecision(typeLength.Value)
                    ? null
                    : $"DECIMAL precision {precision} does not fit in {typeLength} bytes.",
            _ => "DECIMAL requires INT32, INT64, BYTE_ARRAY or FIXED_LEN_BYTE_ARRAY.",
        };
    }

    /// <summary>The largest decimal precision an n-byte two's-complement value can hold: floor(log10(2^(8n-1) - 1)).</summary>
    public static int MaxDecimalPrecision(int byteLength) =>
        (int)Math.Floor((8L * byteLength - 1) * Math.Log10(2));
}
