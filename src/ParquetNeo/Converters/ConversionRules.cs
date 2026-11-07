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

    public static ArgumentException Mismatch(PrimitiveNode column, string dotNetType, string needs) =>
        new($"Column '{string.Join('.', column.Path)}' ({Describe(column)}) cannot be used as {dotNetType}; that needs a {needs}.");

    private static string Describe(PrimitiveNode column) => column.LogicalType is { } logical ? $"{logical.Kind} stored as {column.Type}" : $"{column.Type} without a logical type";
}
