using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Converters;

/// <summary>The checks the converters make of a column before they read or write it as a .NET type.</summary>
internal static class ConversionRules
{
    public static void Require(PrimitiveNode column, PhysicalType physical, LogicalTypeKind kind, string dotNetType, string logical)
    {
        if (column.Type != physical || column.LogicalType?.Kind != kind)
        {
            throw Mismatch(column, dotNetType, $"{logical} column stored as {physical}");
        }
    }

    public static void RequireInteger(PrimitiveNode column, PhysicalType physical, int bitWidth, bool isSigned, string dotNetType)
    {
        if (column.Type != physical || column.LogicalType is not { Kind: LogicalTypeKind.Integer } integer || integer.BitWidth != bitWidth || integer.IsSigned != isSigned)
        {
            throw Mismatch(column, dotNetType, $"{(isSigned ? "signed" : "unsigned")} {bitWidth}-bit INTEGER column stored as {physical}");
        }
    }

    public static void RequireDecimal(PrimitiveNode column, PhysicalType physical)
    {
        if (column.Type != physical || column.LogicalType is not { Kind: LogicalTypeKind.Decimal } decimalType || decimalType.Scale > DecimalConversion.MaxScale)
        {
            throw Mismatch(column, "decimal", $"DECIMAL column with a scale of at most {DecimalConversion.MaxScale} stored as {physical}");
        }
    }

    /// <summary>A string column is a BYTE_ARRAY that is annotated STRING, ENUM or JSON, or not annotated at all (many writers omit it).</summary>
    public static void RequireText(PrimitiveNode column)
    {
        if (column.LogicalType is { Kind: not (LogicalTypeKind.String or LogicalTypeKind.Enum or LogicalTypeKind.Json) })
        {
            throw Mismatch(column, "string", "BYTE_ARRAY column annotated STRING, ENUM or JSON, or not annotated");
        }
    }

    public static ArgumentException Mismatch(PrimitiveNode column, string dotNetType, string needs) =>
        new($"Column '{string.Join('.', column.Path)}' ({Describe(column)}) cannot be used as {dotNetType}; that needs a {needs}.");

    private static string Describe(PrimitiveNode column) => column.LogicalType is { } logical ? $"{logical.Kind} stored as {column.Type}" : $"{column.Type} without a logical type";
}
