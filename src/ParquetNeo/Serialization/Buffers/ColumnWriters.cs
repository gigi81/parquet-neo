using System.Buffers;

namespace ParquetNeo.Serialization.Buffers;

/// <summary>Finds the <see cref="BatchWriter{TValue}"/> for a .NET type: the overload of the column writer that takes it.</summary>
internal static class ColumnWriters
{
    public static BatchWriter<TValue> For<TValue>()
    {
        object writer =
            typeof(TValue) == typeof(bool) ? new BatchWriter<bool>((w, v, d, r) => w.WriteBatch(v, d, r))
            : typeof(TValue) == typeof(sbyte) ? new BatchWriter<sbyte>((w, v, d, r) => w.WriteBatch(v, d, r))
            : typeof(TValue) == typeof(byte) ? new BatchWriter<byte>((w, v, d, r) => w.WriteBatch(v, d, r))
            : typeof(TValue) == typeof(short) ? new BatchWriter<short>((w, v, d, r) => w.WriteBatch(v, d, r))
            : typeof(TValue) == typeof(ushort) ? new BatchWriter<ushort>((w, v, d, r) => w.WriteBatch(v, d, r))
            : typeof(TValue) == typeof(int) ? new BatchWriter<int>((w, v, d, r) => w.WriteBatch(v, d, r))
            : typeof(TValue) == typeof(uint) ? new BatchWriter<uint>((w, v, d, r) => w.WriteBatch(v, d, r))
            : typeof(TValue) == typeof(long) ? new BatchWriter<long>((w, v, d, r) => w.WriteBatch(v, d, r))
            : typeof(TValue) == typeof(ulong) ? new BatchWriter<ulong>((w, v, d, r) => w.WriteBatch(v, d, r))
            : typeof(TValue) == typeof(float) ? new BatchWriter<float>((w, v, d, r) => w.WriteBatch(v, d, r))
            : typeof(TValue) == typeof(double) ? new BatchWriter<double>((w, v, d, r) => w.WriteBatch(v, d, r))
            : typeof(TValue) == typeof(Half) ? new BatchWriter<Half>((w, v, d, r) => w.WriteBatch(v, d, r))
            : typeof(TValue) == typeof(decimal) ? new BatchWriter<decimal>((w, v, d, r) => w.WriteBatch(v, d, r))
            : typeof(TValue) == typeof(string) ? new BatchWriter<string>((w, v, d, r) => w.WriteStrings(v, d, r))
            : typeof(TValue) == typeof(byte[]) ? new BatchWriter<byte[]>(WriteByteArrays)
            : typeof(TValue) == typeof(Guid) ? new BatchWriter<Guid>((w, v, d, r) => w.WriteBatch(v, d, r))
            : typeof(TValue) == typeof(DateTime) ? new BatchWriter<DateTime>((w, v, d, r) => w.WriteBatch(v, d, r))
            : typeof(TValue) == typeof(DateTimeOffset) ? new BatchWriter<DateTimeOffset>((w, v, d, r) => w.WriteBatch(v, d, r))
            : typeof(TValue) == typeof(DateOnly) ? new BatchWriter<DateOnly>((w, v, d, r) => w.WriteBatch(v, d, r))
            : typeof(TValue) == typeof(TimeOnly) ? new BatchWriter<TimeOnly>((w, v, d, r) => w.WriteBatch(v, d, r))
            : throw new NotSupportedException($"{typeof(TValue)} cannot be written as a column.");
        return (BatchWriter<TValue>)writer;
    }

    private static void WriteByteArrays(Core.ColumnWriter writer, ReadOnlySpan<byte[]> values, ReadOnlySpan<int> definitionLevels, ReadOnlySpan<int> repetitionLevels)
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
