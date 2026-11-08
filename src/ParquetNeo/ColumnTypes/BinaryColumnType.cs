using System.Buffers;
using ParquetNeo.Core;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.ColumnTypes;

/// <summary>A BYTE_ARRAY column, plain or annotated BSON, as a <see cref="byte"/> array. Reading copies the bytes out of the page.</summary>
internal sealed class BinaryColumnType : ColumnType<byte[]>
{
    public override string Name => "byte[]";

    public override PrimitiveNode CreateNode(string name, FieldRepetitionType repetition) => new(name, PhysicalType.ByteArray, repetition);

    public override string? GetProblem(PrimitiveNode column) =>
        column.Type == PhysicalType.ByteArray && (column.LogicalType is null || column.LogicalType.Kind == LogicalTypeKind.Bson) ? null : ColumnProblems.Mismatch(column, Name);

    protected override BatchRead ReadCore(ColumnReader reader, Span<byte[]> values, Span<int> definitionLevels, Span<int> repetitionLevels)
    {
        var views = ArrayPool<ReadOnlyMemory<byte>>.Shared.Rent(Math.Max(values.Length, 1));
        try
        {
            var read = reader.ReadByteArrays(views.AsSpan(0, values.Length), definitionLevels, repetitionLevels);
            for (int i = 0; i < read.Values; i++)
            {
                values[i] = views[i].ToArray();
            }

            return read;
        }
        finally
        {
            ArrayPool<ReadOnlyMemory<byte>>.Shared.Return(views, clearArray: true);
        }
    }

    protected override void WriteCore(ColumnWriter writer, ReadOnlySpan<byte[]> values, ReadOnlySpan<int> definitionLevels, ReadOnlySpan<int> repetitionLevels)
    {
        var memories = ArrayPool<ReadOnlyMemory<byte>>.Shared.Rent(Math.Max(values.Length, 1));
        try
        {
            for (int i = 0; i < values.Length; i++)
            {
                memories[i] = values[i];
            }

            writer.WriteByteArrays(memories.AsSpan(0, values.Length), definitionLevels, repetitionLevels);
        }
        finally
        {
            ArrayPool<ReadOnlyMemory<byte>>.Shared.Return(memories, clearArray: true);
        }
    }
}
