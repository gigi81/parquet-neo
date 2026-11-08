using System.Buffers;
using System.Runtime.InteropServices;
using ParquetNeo.Core;

namespace ParquetNeo.ColumnTypes;

/// <summary>
/// The batch loops shared by the column types that convert: read the physical values of a batch into pooled scratch space and
/// convert them one by one, or the other way round. The converter is a struct type argument, so its per-value methods are inlined.
/// </summary>
internal static class PhysicalConversion
{
    public static BatchRead Read<TPhysical, TValue, TConverter>(ColumnReader reader, TConverter converter, Span<TValue> values, Span<int> definitionLevels, Span<int> repetitionLevels)
        where TPhysical : unmanaged
        where TConverter : IValueConverter<TPhysical, TValue>
    {
        var scratch = ArrayPool<TPhysical>.Shared.Rent(Math.Max(values.Length, 1));
        try
        {
            var physical = scratch.AsSpan(0, values.Length);
            BatchRead read = typeof(TPhysical) == typeof(int)
                ? reader.ReadBatch(MemoryMarshal.Cast<TPhysical, int>(physical), definitionLevels, repetitionLevels)
                : reader.ReadBatch(MemoryMarshal.Cast<TPhysical, long>(physical), definitionLevels, repetitionLevels);
            for (int i = 0; i < read.Values; i++)
            {
                values[i] = converter.ToValue(physical[i]);
            }

            return read;
        }
        finally
        {
            ArrayPool<TPhysical>.Shared.Return(scratch);
        }
    }

    public static void Write<TPhysical, TValue, TConverter>(ColumnWriter writer, TConverter converter, ReadOnlySpan<TValue> values, ReadOnlySpan<int> definitionLevels, ReadOnlySpan<int> repetitionLevels)
        where TPhysical : unmanaged
        where TConverter : IValueConverter<TPhysical, TValue>
    {
        var scratch = ArrayPool<TPhysical>.Shared.Rent(Math.Max(values.Length, 1));
        try
        {
            var physical = scratch.AsSpan(0, values.Length);
            for (int i = 0; i < values.Length; i++)
            {
                physical[i] = converter.ToPhysical(values[i]);
            }

            if (typeof(TPhysical) == typeof(int))
            {
                writer.WriteBatch(MemoryMarshal.Cast<TPhysical, int>(physical), definitionLevels, repetitionLevels);
            }
            else
            {
                writer.WriteBatch(MemoryMarshal.Cast<TPhysical, long>(physical), definitionLevels, repetitionLevels);
            }
        }
        finally
        {
            ArrayPool<TPhysical>.Shared.Return(scratch);
        }
    }

    public static BatchRead ReadFixed<TValue, TConverter>(ColumnReader reader, TConverter converter, Span<TValue> values, Span<int> definitionLevels, Span<int> repetitionLevels)
        where TConverter : IFixedLengthConverter<TValue>
    {
        int width = converter.Width;
        var scratch = ArrayPool<byte>.Shared.Rent(Math.Max(values.Length * width, 1));
        try
        {
            BatchRead read = reader.ReadFixedLengthByteArrays(scratch.AsSpan(0, values.Length * width), definitionLevels, repetitionLevels);
            for (int i = 0; i < read.Values; i++)
            {
                values[i] = converter.ToValue(scratch.AsSpan(i * width, width));
            }

            return read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    public static void WriteFixed<TValue, TConverter>(ColumnWriter writer, TConverter converter, ReadOnlySpan<TValue> values, ReadOnlySpan<int> definitionLevels, ReadOnlySpan<int> repetitionLevels)
        where TConverter : IFixedLengthConverter<TValue>
    {
        int width = converter.Width;
        var scratch = ArrayPool<byte>.Shared.Rent(Math.Max(values.Length * width, 1));
        try
        {
            for (int i = 0; i < values.Length; i++)
            {
                converter.ToBytes(values[i], scratch.AsSpan(i * width, width));
            }

            writer.WriteFixedLengthByteArrays(scratch.AsSpan(0, values.Length * width), definitionLevels, repetitionLevels);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }
}
