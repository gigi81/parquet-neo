using System.Buffers;
using ParquetNeo.Core;

namespace ParquetNeo.Serialization.Columns;

/// <summary>Finds the <see cref="BatchReader{TValue}"/> for a .NET type: the overload of the column reader that gives it.</summary>
internal static class ColumnReaders
{
    public static BatchReader<TValue> For<TValue>()
    {
        object reader =
            typeof(TValue) == typeof(bool) ? new BatchReader<bool>((r, v, d, p) => r.ReadBatch(v, d, p))
            : typeof(TValue) == typeof(sbyte) ? new BatchReader<sbyte>((r, v, d, p) => r.ReadBatch(v, d, p))
            : typeof(TValue) == typeof(byte) ? new BatchReader<byte>((r, v, d, p) => r.ReadBatch(v, d, p))
            : typeof(TValue) == typeof(short) ? new BatchReader<short>((r, v, d, p) => r.ReadBatch(v, d, p))
            : typeof(TValue) == typeof(ushort) ? new BatchReader<ushort>((r, v, d, p) => r.ReadBatch(v, d, p))
            : typeof(TValue) == typeof(int) ? new BatchReader<int>((r, v, d, p) => r.ReadBatch(v, d, p))
            : typeof(TValue) == typeof(uint) ? new BatchReader<uint>((r, v, d, p) => r.ReadBatch(v, d, p))
            : typeof(TValue) == typeof(long) ? new BatchReader<long>((r, v, d, p) => r.ReadBatch(v, d, p))
            : typeof(TValue) == typeof(ulong) ? new BatchReader<ulong>((r, v, d, p) => r.ReadBatch(v, d, p))
            : typeof(TValue) == typeof(float) ? new BatchReader<float>((r, v, d, p) => r.ReadBatch(v, d, p))
            : typeof(TValue) == typeof(double) ? new BatchReader<double>((r, v, d, p) => r.ReadBatch(v, d, p))
            : typeof(TValue) == typeof(Half) ? new BatchReader<Half>((r, v, d, p) => r.ReadBatch(v, d, p))
            : typeof(TValue) == typeof(decimal) ? new BatchReader<decimal>((r, v, d, p) => r.ReadBatch(v, d, p))
            : typeof(TValue) == typeof(string) ? new BatchReader<string>((r, v, d, p) => r.ReadStrings(v, d, p))
            : typeof(TValue) == typeof(byte[]) ? new BatchReader<byte[]>(ReadByteArrays)
            : typeof(TValue) == typeof(Guid) ? new BatchReader<Guid>((r, v, d, p) => r.ReadBatch(v, d, p))
            : typeof(TValue) == typeof(DateTime) ? new BatchReader<DateTime>((r, v, d, p) => r.ReadBatch(v, d, p))
            : typeof(TValue) == typeof(DateTimeOffset) ? new BatchReader<DateTimeOffset>((r, v, d, p) => r.ReadBatch(v, d, p))
            : typeof(TValue) == typeof(DateOnly) ? new BatchReader<DateOnly>((r, v, d, p) => r.ReadBatch(v, d, p))
            : typeof(TValue) == typeof(TimeOnly) ? new BatchReader<TimeOnly>((r, v, d, p) => r.ReadBatch(v, d, p))
            : throw new NotSupportedException($"{typeof(TValue)} cannot be read from a column.");
        return (BatchReader<TValue>)reader;
    }

    /// <summary>The views the column reader gives into its page are copied, since they are only valid until the next read.</summary>
    private static BatchRead ReadByteArrays(ColumnReader reader, Span<byte[]> values, Span<int> definitionLevels, Span<int> repetitionLevels)
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
}
