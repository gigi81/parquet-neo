using System.Buffers;
using ParquetNeo.Core;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.ColumnTypes;

/// <summary>
/// A DECIMAL column of any physical type (INT32, INT64, FIXED_LEN_BYTE_ARRAY or BYTE_ARRAY) as <see cref="decimal"/>. Digits are never
/// lost: a value with more decimals than the column's scale, or more digits than its precision, cannot be written. A
/// <see cref="decimal"/> holds 96 bits and a scale of up to 28, so a column with a larger scale does not fit, and a larger stored
/// value cannot be read. The precision and scale of the column it creates are its settings (38 and 18 by default); the column is
/// INT32 up to a precision of 9, INT64 up to 18, and otherwise a FIXED_LEN_BYTE_ARRAY as short as holds the precision.
/// </summary>
internal sealed class DecimalColumnType(int precision = DecimalColumnType.DefaultPrecision, int scale = DecimalColumnType.DefaultScale) : ColumnType<decimal>
{
    public const int DefaultPrecision = 38;
    public const int DefaultScale = 18;

    public override string Name => "decimal";

    public override ColumnSettingKinds SupportedSettings => ColumnSettingKinds.Decimal;

    public override ColumnType<decimal> Configure(ColumnSettings settings) => new DecimalColumnType(settings.Precision ?? precision, settings.Scale ?? scale);

    public override PrimitiveNode CreateNode(string name, FieldRepetitionType repetition)
    {
        var logical = LogicalType.Decimal(scale, precision);
        if (precision <= 9)
        {
            return new(name, PhysicalType.Int32, repetition, logical);
        }

        if (precision <= 18)
        {
            return new(name, PhysicalType.Int64, repetition, logical);
        }

        int length = 1;
        while (LogicalTypeRules.MaxDecimalPrecision(length) < precision)
        {
            length++;
        }

        return new(name, PhysicalType.FixedLenByteArray, repetition, logical, typeLength: length);
    }

    public override string? GetProblem(PrimitiveNode column) =>
        column.Type is PhysicalType.Int32 or PhysicalType.Int64 or PhysicalType.FixedLenByteArray or PhysicalType.ByteArray
        && column.LogicalType is { Kind: LogicalTypeKind.Decimal } logical
        && logical.Scale <= DecimalConversion.MaxScale
            ? null
            : $"{ColumnProblems.Mismatch(column, Name)} (it needs a DECIMAL with a scale of at most {DecimalConversion.MaxScale}, stored as INT32, INT64, FIXED_LEN_BYTE_ARRAY or BYTE_ARRAY)";

    protected override BatchRead ReadCore(ColumnReader reader, Span<decimal> values, Span<int> definitionLevels, Span<int> repetitionLevels)
    {
        var column = reader.Column;
        return column.Type switch
        {
            PhysicalType.Int32 => PhysicalConversion.Read<int, decimal, Decimal32Converter>(reader, Decimal32Converter.Create(column), values, definitionLevels, repetitionLevels),
            PhysicalType.Int64 => PhysicalConversion.Read<long, decimal, Decimal64Converter>(reader, Decimal64Converter.Create(column), values, definitionLevels, repetitionLevels),
            PhysicalType.FixedLenByteArray => PhysicalConversion.ReadFixed<decimal, FixedConverter>(reader, FixedConverter.Create(column), values, definitionLevels, repetitionLevels),
            _ => ReadByteArrays(reader, values, definitionLevels, repetitionLevels),
        };
    }

    protected override void WriteCore(ColumnWriter writer, ReadOnlySpan<decimal> values, ReadOnlySpan<int> definitionLevels, ReadOnlySpan<int> repetitionLevels)
    {
        var column = writer.Column;
        switch (column.Type)
        {
            case PhysicalType.Int32:
                PhysicalConversion.Write<int, decimal, Decimal32Converter>(writer, Decimal32Converter.Create(column), values, definitionLevels, repetitionLevels);
                break;
            case PhysicalType.Int64:
                PhysicalConversion.Write<long, decimal, Decimal64Converter>(writer, Decimal64Converter.Create(column), values, definitionLevels, repetitionLevels);
                break;
            case PhysicalType.FixedLenByteArray:
                PhysicalConversion.WriteFixed<decimal, FixedConverter>(writer, FixedConverter.Create(column), values, definitionLevels, repetitionLevels);
                break;
            default:
                WriteByteArrays(writer, values, definitionLevels, repetitionLevels);
                break;
        }
    }

    private static BatchRead ReadByteArrays(ColumnReader reader, Span<decimal> values, Span<int> definitionLevels, Span<int> repetitionLevels)
    {
        var converter = ByteArrayConverter.Create(reader.Column);
        var scratch = ArrayPool<ReadOnlyMemory<byte>>.Shared.Rent(Math.Max(values.Length, 1));
        try
        {
            BatchRead read = reader.ReadByteArrays(scratch.AsSpan(0, values.Length), definitionLevels, repetitionLevels);
            for (int i = 0; i < read.Values; i++)
            {
                values[i] = converter.ToValue(scratch[i].Span);
            }

            return read;
        }
        finally
        {
            ArrayPool<ReadOnlyMemory<byte>>.Shared.Return(scratch, clearArray: true);
        }
    }

    private static void WriteByteArrays(ColumnWriter writer, ReadOnlySpan<decimal> values, ReadOnlySpan<int> definitionLevels, ReadOnlySpan<int> repetitionLevels)
    {
        var converter = ByteArrayConverter.Create(writer.Column);
        var bytes = ArrayPool<byte>.Shared.Rent(Math.Max(values.Length * ByteArrayConverter.MaxLength, 1));
        var arrays = ArrayPool<ReadOnlyMemory<byte>>.Shared.Rent(Math.Max(values.Length, 1));
        try
        {
            for (int i = 0; i < values.Length; i++)
            {
                int offset = i * ByteArrayConverter.MaxLength;
                int length = converter.ToBytes(values[i], bytes.AsSpan(offset, ByteArrayConverter.MaxLength));
                arrays[i] = new ReadOnlyMemory<byte>(bytes, offset, length);
            }

            writer.WriteByteArrays(arrays.AsSpan(0, values.Length), definitionLevels, repetitionLevels);
        }
        finally
        {
            ArrayPool<ReadOnlyMemory<byte>>.Shared.Return(arrays, clearArray: true);
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    /// <summary>A DECIMAL stored as INT32 (precision up to 9).</summary>
    internal readonly struct Decimal32Converter : IValueConverter<int, decimal>
    {
        private readonly int _scale;
        private readonly int _precision;

        private Decimal32Converter(int scale, int precision)
        {
            _scale = scale;
            _precision = precision;
        }

        public static Decimal32Converter Create(PrimitiveNode column) => new(column.LogicalType!.Scale, column.LogicalType.Precision);

        public decimal ToValue(int physical) => DecimalConversion.FromInt64(physical, _scale);

        public int ToPhysical(decimal value) => (int)DecimalConversion.ToUnscaled(value, _scale, _precision);
    }

    /// <summary>A DECIMAL stored as INT64 (precision up to 18).</summary>
    internal readonly struct Decimal64Converter : IValueConverter<long, decimal>
    {
        private readonly int _scale;
        private readonly int _precision;

        private Decimal64Converter(int scale, int precision)
        {
            _scale = scale;
            _precision = precision;
        }

        public static Decimal64Converter Create(PrimitiveNode column) => new(column.LogicalType!.Scale, column.LogicalType.Precision);

        public decimal ToValue(long physical) => DecimalConversion.FromInt64(physical, _scale);

        public long ToPhysical(decimal value) => (long)DecimalConversion.ToUnscaled(value, _scale, _precision);
    }

    /// <summary>A DECIMAL stored as FIXED_LEN_BYTE_ARRAY: big-endian two's complement.</summary>
    internal readonly struct FixedConverter : IFixedLengthConverter<decimal>
    {
        private readonly int _scale;
        private readonly int _precision;

        private FixedConverter(int width, int scale, int precision)
        {
            Width = width;
            _scale = scale;
            _precision = precision;
        }

        public static FixedConverter Create(PrimitiveNode column) => new(column.TypeLength!.Value, column.LogicalType!.Scale, column.LogicalType.Precision);

        public int Width { get; }

        public decimal ToValue(ReadOnlySpan<byte> bytes) => DecimalConversion.FromBigEndian(bytes, _scale);

        public void ToBytes(decimal value, Span<byte> bytes) =>
            DecimalConversion.WriteBigEndian(DecimalConversion.ToUnscaled(value, _scale, _precision), bytes);
    }

    /// <summary>
    /// A DECIMAL stored as BYTE_ARRAY. The values have no fixed width, so this does not implement
    /// <see cref="IFixedLengthConverter{TValue}"/>; the column type handles the arrays itself.
    /// </summary>
    internal readonly struct ByteArrayConverter
    {
        /// <summary>The most bytes the shortest form of a value takes.</summary>
        public const int MaxLength = 16;

        private readonly int _scale;
        private readonly int _precision;

        private ByteArrayConverter(int scale, int precision)
        {
            _scale = scale;
            _precision = precision;
        }

        public static ByteArrayConverter Create(PrimitiveNode column) => new(column.LogicalType!.Scale, column.LogicalType.Precision);

        public decimal ToValue(ReadOnlySpan<byte> bytes) => DecimalConversion.FromBigEndian(bytes, _scale);

        /// <summary>Writes the value in its shortest form to the start of <paramref name="bytes"/> (at least <see cref="MaxLength"/> long) and returns the length used.</summary>
        public int ToBytes(decimal value, Span<byte> bytes)
        {
            var unscaled = DecimalConversion.ToUnscaled(value, _scale, _precision);
            int length = DecimalConversion.MinimalLength(unscaled);
            DecimalConversion.WriteBigEndian(unscaled, bytes[..length]);
            return length;
        }
    }
}
