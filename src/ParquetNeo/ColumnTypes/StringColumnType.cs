using System.Buffers;
using System.Text;
using ParquetNeo.Core;
using ParquetNeo.Exceptions;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.ColumnTypes;

/// <summary>
/// A BYTE_ARRAY column annotated STRING, ENUM or JSON, or not annotated (many writers omit it), as UTF-8 text. Bytes that are not
/// valid UTF-8 are a <see cref="ValueConversionException"/> when reading.
/// </summary>
internal sealed class StringColumnType : ColumnType<string>
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public override string Name => "string";

    public override PrimitiveNode CreateNode(string name, FieldRepetitionType repetition) => new(name, PhysicalType.ByteArray, repetition, LogicalType.String);

    public override string? GetProblem(PrimitiveNode column) => IsText(column) ? null : ColumnProblems.Mismatch(column, Name);

    /// <summary>Whether the column holds text: a BYTE_ARRAY annotated STRING, ENUM or JSON, or not annotated at all.</summary>
    internal static bool IsText(PrimitiveNode column) =>
        column.Type == PhysicalType.ByteArray && (column.LogicalType is null || column.LogicalType.Kind is LogicalTypeKind.String or LogicalTypeKind.Enum or LogicalTypeKind.Json);

    protected override BatchRead ReadCore(ColumnReader reader, Span<string> values, Span<int> definitionLevels, Span<int> repetitionLevels)
    {
        var scratch = ArrayPool<ReadOnlyMemory<byte>>.Shared.Rent(Math.Max(values.Length, 1));
        try
        {
            BatchRead read = reader.ReadByteArrays(scratch.AsSpan(0, values.Length), definitionLevels, repetitionLevels);
            for (int i = 0; i < read.Values; i++)
            {
                try
                {
                    values[i] = StrictUtf8.GetString(scratch[i].Span);
                }
                catch (DecoderFallbackException exception)
                {
                    throw new ValueConversionException($"Column '{reader.Column.Name}' holds text that is not valid UTF-8.", exception);
                }
            }

            return read;
        }
        finally
        {
            ArrayPool<ReadOnlyMemory<byte>>.Shared.Return(scratch, clearArray: true);
        }
    }

    protected override void WriteCore(ColumnWriter writer, ReadOnlySpan<string> values, ReadOnlySpan<int> definitionLevels, ReadOnlySpan<int> repetitionLevels) =>
        writer.WriteUtf8Strings(values, definitionLevels, repetitionLevels);
}
