using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.ColumnTypes;

/// <summary>The words the column types use to say why a column does not fit them.</summary>
internal static class ColumnProblems
{
    /// <summary>Says what the column is, and that the type does not fit it.</summary>
    public static string Mismatch(PrimitiveNode column, string typeName) => $"it is {Describe(column)}, which does not fit {typeName}";

    public static string Describe(PrimitiveNode column) =>
        column.LogicalType is { } logical ? $"{Describe(column.Type)} annotated {Describe(logical)}" : Describe(column.Type);

    public static string Describe(PhysicalType type) => type switch
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
}
