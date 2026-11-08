using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.ColumnTypes;

/// <summary>An unsigned 32-bit INTEGER column (UINT_32), stored as the signed bits of an INT32, as <see cref="uint"/>.</summary>
internal sealed class UInt32ColumnType : ConvertedColumnType<uint, int, UInt32ColumnType.Converter>
{
    public override string Name => "uint";

    public override PrimitiveNode CreateNode(string name, FieldRepetitionType repetition) => new(name, PhysicalType.Int32, repetition, LogicalType.Integer(32, false));

    public override string? GetProblem(PrimitiveNode column) =>
        column.Type == PhysicalType.Int32 && column.LogicalType is { Kind: LogicalTypeKind.Integer, BitWidth: 32, IsSigned: false } ? null : ColumnProblems.Mismatch(column, Name);

    internal readonly struct Converter : IValueConverter<int, uint, Converter>
    {
        public static Converter Create(PrimitiveNode column) => default;

        public uint ToValue(int physical) => unchecked((uint)physical);

        public int ToPhysical(uint value) => unchecked((int)value);
    }
}
