using ParquetNeo.Exceptions;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.ColumnTypes;

/// <summary>A signed 8-bit INTEGER column (INT_8) as <see cref="sbyte"/>.</summary>
internal sealed class SByteColumnType : ConvertedColumnType<sbyte, int, SByteColumnType.Converter>
{
    public override string Name => "sbyte";

    public override PrimitiveNode CreateNode(string name, FieldRepetitionType repetition) => new(name, PhysicalType.Int32, repetition, LogicalType.Integer(8, true));

    public override string? GetProblem(PrimitiveNode column) =>
        column.Type == PhysicalType.Int32 && column.LogicalType is { Kind: LogicalTypeKind.Integer, BitWidth: 8, IsSigned: true }
            ? null
            : ColumnProblems.Mismatch(column, Name);

    internal readonly struct Converter : IValueConverter<int, sbyte, Converter>
    {
        public static Converter Create(PrimitiveNode column) => default;

        public sbyte ToValue(int physical) => physical is >= sbyte.MinValue and <= sbyte.MaxValue ? (sbyte)physical : throw OutOfRange(physical);

        public int ToPhysical(sbyte value) => value;

        private static ValueConversionException OutOfRange(int physical) =>
            new($"The value {physical} is outside the range of the {typeof(sbyte).Name} its column is stored as.");
    }
}
