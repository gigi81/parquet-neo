using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.ColumnTypes;

/// <summary>An unsigned 64-bit INTEGER column (UINT_64), stored as the signed bits of an INT64, as <see cref="ulong"/>.</summary>
internal sealed class UInt64ColumnType : ConvertedColumnType<ulong, long, UInt64ColumnType.Converter>
{
    public override string Name => "ulong";

    public override PrimitiveNode CreateNode(string name, FieldRepetitionType repetition) => new(name, PhysicalType.Int64, repetition, LogicalType.Integer(64, false));

    public override string? GetProblem(PrimitiveNode column) =>
        column.Type == PhysicalType.Int64 && column.LogicalType is { Kind: LogicalTypeKind.Integer, BitWidth: 64, IsSigned: false } ? null : ColumnProblems.Mismatch(column, Name);

    internal readonly struct Converter : IValueConverter<long, ulong, Converter>
    {
        public static Converter Create(PrimitiveNode column) => default;

        public ulong ToValue(long physical) => unchecked((ulong)physical);

        public long ToPhysical(ulong value) => unchecked((long)value);
    }
}
