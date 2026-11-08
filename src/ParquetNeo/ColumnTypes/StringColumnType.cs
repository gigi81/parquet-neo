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

    /// <summary>The text of UTF-8 bytes, which must be valid.</summary>
    /// <exception cref="ValueConversionException">The bytes are not valid UTF-8.</exception>
    internal static string Decode(ReadOnlySpan<byte> bytes, string columnName)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ValueConversionException($"Column '{columnName}' holds text that is not valid UTF-8.", exception);
        }
    }

    protected override BatchRead ReadCore(ColumnReader reader, Span<string> values, Span<int> definitionLevels, Span<int> repetitionLevels) =>
        reader.ReadUtf8Strings(values, definitionLevels, repetitionLevels);

    protected override void WriteCore(ColumnWriter writer, ReadOnlySpan<string> values, ReadOnlySpan<int> definitionLevels, ReadOnlySpan<int> repetitionLevels) =>
        writer.WriteUtf8Strings(values, definitionLevels, repetitionLevels);
}
