using System.Buffers;
using ParquetNeo.Core;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.ColumnTypes;

/// <summary>
/// A BYTE_ARRAY column, plain or annotated BSON, or a FIXED_LEN_BYTE_ARRAY column without an annotation, as a <see cref="byte"/>
/// array. Reading copies the bytes out of the page. A fixed-length column holds values of exactly its length, so writing an array
/// of another length to it is an <see cref="ArgumentOutOfRangeException"/>. The column it creates is a BYTE_ARRAY.
/// </summary>
internal sealed class BinaryColumnType : ColumnType<byte[]>
{
    public override string Name => "byte[]";

    public override PrimitiveNode CreateNode(string name, FieldRepetitionType repetition) => new(name, PhysicalType.ByteArray, repetition);

    public override string? GetProblem(PrimitiveNode column)
    {
        bool fits = column.Type switch
        {
            PhysicalType.ByteArray => column.LogicalType is null || column.LogicalType.Kind == LogicalTypeKind.Bson,
            PhysicalType.FixedLenByteArray => column.LogicalType is null,
            _ => false,
        };
        return fits ? null : ColumnProblems.Mismatch(column, Name);
    }

    protected override BatchRead ReadCore(ColumnReader reader, Span<byte[]> values, Span<int> definitionLevels, Span<int> repetitionLevels)
    {
        if (reader.Column.Type == PhysicalType.FixedLenByteArray)
        {
            return ReadFixed(reader, values, definitionLevels, repetitionLevels);
        }

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
        if (writer.Column.Type == PhysicalType.FixedLenByteArray)
        {
            WriteFixed(writer, values, definitionLevels, repetitionLevels);
            return;
        }

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

    private static BatchRead ReadFixed(ColumnReader reader, Span<byte[]> values, Span<int> definitionLevels, Span<int> repetitionLevels)
    {
        int width = reader.Column.TypeLength!.Value;
        var scratch = ArrayPool<byte>.Shared.Rent(Math.Max(checked(values.Length * width), 1));
        try
        {
            var read = reader.ReadFixedLengthByteArrays(scratch.AsSpan(0, values.Length * width), definitionLevels, repetitionLevels);
            for (int i = 0; i < read.Values; i++)
            {
                values[i] = scratch.AsSpan(i * width, width).ToArray();
            }

            return read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    private static void WriteFixed(ColumnWriter writer, ReadOnlySpan<byte[]> values, ReadOnlySpan<int> definitionLevels, ReadOnlySpan<int> repetitionLevels)
    {
        int width = writer.Column.TypeLength!.Value;
        var scratch = ArrayPool<byte>.Shared.Rent(Math.Max(checked(values.Length * width), 1));
        try
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] is not { } value || value.Length != width)
                {
                    throw new ArgumentOutOfRangeException(nameof(values), $"Value {i} has {values[i]?.Length ?? 0} bytes, but the column holds values of exactly {width} bytes.");
                }

                value.CopyTo(scratch, i * width);
            }

            writer.WriteFixedLengthByteArrays(scratch.AsSpan(0, values.Length * width), definitionLevels, repetitionLevels);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }
}
