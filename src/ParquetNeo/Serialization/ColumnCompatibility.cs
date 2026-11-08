using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Serialization;

/// <summary>Tells whether a column of a file can be read as the .NET type of a property, which is when the typed overloads of the column reader accept it.</summary>
internal static class ColumnCompatibility
{
    /// <summary>Why the column cannot be read as <paramref name="type"/>, or null when it can.</summary>
    /// <param name="repetition">The number of collections the type is in: the number of repeated nodes the column must have.</param>
    public static string? Problem(PrimitiveNode column, Type type, int repetition)
    {
        if (column.MaxRepetitionLevel != repetition)
        {
            return repetition == 0
                ? "it is repeated, and only the elements of a collection can be read from a repeated column"
                : $"it is repeated {column.MaxRepetitionLevel} times, but the property is in {repetition} collections";
        }

        var logical = column.LogicalType;
        var physical = column.Type;
        bool fits = type switch
        {
            _ when type == typeof(bool) => physical == PhysicalType.Boolean,
            _ when type == typeof(sbyte) => physical == PhysicalType.Int32 && logical is { Kind: LogicalTypeKind.Integer, BitWidth: 8, IsSigned: true },
            _ when type == typeof(byte) => physical == PhysicalType.Int32 && logical is { Kind: LogicalTypeKind.Integer, BitWidth: 8, IsSigned: false },
            _ when type == typeof(short) => physical == PhysicalType.Int32 && logical is { Kind: LogicalTypeKind.Integer, BitWidth: 16, IsSigned: true },
            _ when type == typeof(ushort) => physical == PhysicalType.Int32 && logical is { Kind: LogicalTypeKind.Integer, BitWidth: 16, IsSigned: false },
            _ when type == typeof(int) => physical == PhysicalType.Int32 && (logical is null || logical is { Kind: LogicalTypeKind.Integer, IsSigned: true }),
            _ when type == typeof(uint) => physical == PhysicalType.Int32 && logical is { Kind: LogicalTypeKind.Integer, BitWidth: 32, IsSigned: false },
            _ when type == typeof(long) => physical == PhysicalType.Int64 && (logical is null || logical is { Kind: LogicalTypeKind.Integer, IsSigned: true }),
            _ when type == typeof(ulong) => physical == PhysicalType.Int64 && logical is { Kind: LogicalTypeKind.Integer, BitWidth: 64, IsSigned: false },
            _ when type == typeof(float) => physical == PhysicalType.Float,
            _ when type == typeof(double) => physical == PhysicalType.Double,
            _ when type == typeof(Half) => physical == PhysicalType.FixedLenByteArray && logical is { Kind: LogicalTypeKind.Float16 },
            _ when type == typeof(decimal) => logical is { Kind: LogicalTypeKind.Decimal, Scale: <= 28 },
            _ when type == typeof(string) || type.IsEnum => physical == PhysicalType.ByteArray && (logical is null || logical.Kind is LogicalTypeKind.String or LogicalTypeKind.Enum or LogicalTypeKind.Json),
            _ when type == typeof(byte[]) => physical == PhysicalType.ByteArray && (logical is null || logical.Kind == LogicalTypeKind.Bson),
            _ when type == typeof(Guid) => physical == PhysicalType.FixedLenByteArray && logical is { Kind: LogicalTypeKind.Uuid },
            _ when type == typeof(DateTime) || type == typeof(DateTimeOffset) =>
                (physical == PhysicalType.Int64 && logical is { Kind: LogicalTypeKind.Timestamp }) || physical == PhysicalType.Int96,
            _ when type == typeof(DateOnly) => physical == PhysicalType.Int32 && logical is { Kind: LogicalTypeKind.Date },
            _ when type == typeof(TimeOnly) => logical is { Kind: LogicalTypeKind.Time },
            _ => false,
        };

        return fits ? null : $"it is {Describe(column)}, which cannot be read as {Name(type)}";
    }

    private static string Describe(PrimitiveNode column) =>
        column.LogicalType is { } logical ? $"{Describe(column.Type)} annotated {Describe(logical)}" : Describe(column.Type);

    private static string Describe(PhysicalType type) => type switch
    {
        PhysicalType.ByteArray => "BYTE_ARRAY",
        PhysicalType.FixedLenByteArray => "FIXED_LEN_BYTE_ARRAY",
        _ => type.ToString().ToUpperInvariant(),
    };

    private static string Describe(LogicalType logical) => logical.Kind switch
    {
        LogicalTypeKind.Integer => $"{(logical.IsSigned ? "signed" : "unsigned")} {logical.BitWidth}-bit INTEGER",
        LogicalTypeKind.Decimal => $"DECIMAL({logical.Precision}, {logical.Scale})",
        LogicalTypeKind.Timestamp => $"TIMESTAMP({logical.Unit})",
        LogicalTypeKind.Time => $"TIME({logical.Unit})",
        _ => logical.Kind.ToString().ToUpperInvariant(),
    };

    private static string Name(Type type) => type.IsEnum ? $"the enum {type.Name}" : type == typeof(byte[]) ? "byte[]" : type.Name;
}
