using System.Buffers;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.ColumnTypes;

/// <summary>
/// An enum as the name of its value: a BYTE_ARRAY annotated ENUM, the way other Parquet writers store one. Any text column can be
/// read as an enum, and a name that is no value of it is a <see cref="Exceptions.ValueConversionException"/>.
/// </summary>
internal sealed class EnumColumnType<TEnum> : ColumnType<TEnum>
    where TEnum : struct, Enum
{
    private static readonly StringColumnType Text = new();

    public override string Name => $"the enum {typeof(TEnum).Name}";

    public override PrimitiveNode CreateNode(string name, FieldRepetitionType repetition) => new(name, PhysicalType.ByteArray, repetition, LogicalType.Enum);

    public override string? GetProblem(PrimitiveNode column) => StringColumnType.IsText(column) ? null : ColumnProblems.Mismatch(column, Name);

    protected override BatchRead ReadCore(ColumnReader reader, Span<TEnum> values, Span<int> definitionLevels, Span<int> repetitionLevels)
    {
        var names = ArrayPool<string>.Shared.Rent(Math.Max(values.Length, 1));
        try
        {
            BatchRead read = Text.Read(reader, names.AsSpan(0, values.Length), definitionLevels, repetitionLevels);
            for (int i = 0; i < read.Values; i++)
            {
                values[i] = EnumParser<TEnum>.Parse(names[i]);
            }

            return read;
        }
        finally
        {
            ArrayPool<string>.Shared.Return(names, clearArray: true);
        }
    }

    protected override void WriteCore(ColumnWriter writer, ReadOnlySpan<TEnum> values, ReadOnlySpan<int> definitionLevels, ReadOnlySpan<int> repetitionLevels)
    {
        var names = ArrayPool<string>.Shared.Rent(Math.Max(values.Length, 1));
        try
        {
            for (int i = 0; i < values.Length; i++)
            {
                names[i] = EnumNames<TEnum>.Of(values[i]);
            }

            Text.Write(writer, names.AsSpan(0, values.Length), definitionLevels, repetitionLevels);
        }
        finally
        {
            ArrayPool<string>.Shared.Return(names, clearArray: true);
        }
    }
}
