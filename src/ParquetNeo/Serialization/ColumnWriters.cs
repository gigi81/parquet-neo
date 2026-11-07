using System.Buffers;

namespace ParquetNeo.Serialization;

/// <summary>Finds the <see cref="BatchWriter{TValue}"/> for a .NET type: the overload of the column writer that takes it.</summary>
internal static class ColumnWriters
{
    public static BatchWriter<TValue> For<TValue>()
    {
        object writer =
            typeof(TValue) == typeof(bool) ? new BatchWriter<bool>((w, v, l) => w.WriteBatch(v, l))
            : typeof(TValue) == typeof(sbyte) ? new BatchWriter<sbyte>((w, v, l) => w.WriteBatch(v, l))
            : typeof(TValue) == typeof(byte) ? new BatchWriter<byte>((w, v, l) => w.WriteBatch(v, l))
            : typeof(TValue) == typeof(short) ? new BatchWriter<short>((w, v, l) => w.WriteBatch(v, l))
            : typeof(TValue) == typeof(ushort) ? new BatchWriter<ushort>((w, v, l) => w.WriteBatch(v, l))
            : typeof(TValue) == typeof(int) ? new BatchWriter<int>((w, v, l) => w.WriteBatch(v, l))
            : typeof(TValue) == typeof(uint) ? new BatchWriter<uint>((w, v, l) => w.WriteBatch(v, l))
            : typeof(TValue) == typeof(long) ? new BatchWriter<long>((w, v, l) => w.WriteBatch(v, l))
            : typeof(TValue) == typeof(ulong) ? new BatchWriter<ulong>((w, v, l) => w.WriteBatch(v, l))
            : typeof(TValue) == typeof(float) ? new BatchWriter<float>((w, v, l) => w.WriteBatch(v, l))
            : typeof(TValue) == typeof(double) ? new BatchWriter<double>((w, v, l) => w.WriteBatch(v, l))
            : typeof(TValue) == typeof(Half) ? new BatchWriter<Half>((w, v, l) => w.WriteBatch(v, l))
            : typeof(TValue) == typeof(decimal) ? new BatchWriter<decimal>((w, v, l) => w.WriteBatch(v, l))
            : typeof(TValue) == typeof(string) ? new BatchWriter<string>((w, v, l) => w.WriteStrings(v, l))
            : typeof(TValue) == typeof(byte[]) ? new BatchWriter<byte[]>(WriteByteArrays)
            : typeof(TValue) == typeof(Guid) ? new BatchWriter<Guid>((w, v, l) => w.WriteBatch(v, l))
            : typeof(TValue) == typeof(DateTime) ? new BatchWriter<DateTime>((w, v, l) => w.WriteBatch(v, l))
            : typeof(TValue) == typeof(DateTimeOffset) ? new BatchWriter<DateTimeOffset>((w, v, l) => w.WriteBatch(v, l))
            : typeof(TValue) == typeof(DateOnly) ? new BatchWriter<DateOnly>((w, v, l) => w.WriteBatch(v, l))
            : typeof(TValue) == typeof(TimeOnly) ? new BatchWriter<TimeOnly>((w, v, l) => w.WriteBatch(v, l))
            : throw new NotSupportedException($"{typeof(TValue)} cannot be written as a column.");
        return (BatchWriter<TValue>)writer;
    }

    private static void WriteByteArrays(Core.ColumnWriter writer, ReadOnlySpan<byte[]> values, ReadOnlySpan<int> definitionLevels)
    {
        var memories = ArrayPool<ReadOnlyMemory<byte>>.Shared.Rent(Math.Max(values.Length, 1));
        try
        {
            for (int i = 0; i < values.Length; i++)
            {
                memories[i] = values[i];
            }

            writer.WriteByteArrays(memories.AsSpan(0, values.Length), definitionLevels);
        }
        finally
        {
            ArrayPool<ReadOnlyMemory<byte>>.Shared.Return(memories, clearArray: true);
        }
    }
}
