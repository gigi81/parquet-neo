using System.Buffers;
using System.Runtime.InteropServices;
using ParquetNeo.Converters;
using ParquetNeo.Metadata;

namespace ParquetNeo.Core;

/// <summary>
/// Writing columns with a logical type from the .NET type it stands for; the counterpart of the logical overloads of
/// <see cref="ColumnReader"/>. Each overload checks the column's logical type and throws <see cref="ArgumentException"/>
/// for a column of another type. Levels work as in the physical overloads.
/// </summary>
public sealed partial class ColumnWriter
{
    /// <summary>Writes an INT_8 column.</summary>
    public void WriteBatch(ReadOnlySpan<sbyte> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default) =>
        WriteConverted<int, sbyte, SByteConverter>(values, definitionLevels, repetitionLevels);

    /// <summary>Writes a UINT_8 column.</summary>
    public void WriteBatch(ReadOnlySpan<byte> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default) =>
        WriteConverted<int, byte, ByteConverter>(values, definitionLevels, repetitionLevels);

    /// <summary>Writes an INT_16 column.</summary>
    public void WriteBatch(ReadOnlySpan<short> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default) =>
        WriteConverted<int, short, Int16Converter>(values, definitionLevels, repetitionLevels);

    /// <summary>Writes a UINT_16 column.</summary>
    public void WriteBatch(ReadOnlySpan<ushort> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default) =>
        WriteConverted<int, ushort, UInt16Converter>(values, definitionLevels, repetitionLevels);

    /// <summary>Writes a UINT_32 column.</summary>
    public void WriteBatch(ReadOnlySpan<uint> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default) =>
        WriteConverted<int, uint, UInt32Converter>(values, definitionLevels, repetitionLevels);

    /// <summary>Writes a UINT_64 column.</summary>
    public void WriteBatch(ReadOnlySpan<ulong> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default) =>
        WriteConverted<long, ulong, UInt64Converter>(values, definitionLevels, repetitionLevels);

    /// <summary>Writes a DATE column.</summary>
    public void WriteBatch(ReadOnlySpan<DateOnly> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default) =>
        WriteConverted<int, DateOnly, DateConverter>(values, definitionLevels, repetitionLevels);

    /// <summary>Writes a TIME column in any unit; times are cut to the unit.</summary>
    public void WriteBatch(ReadOnlySpan<TimeOnly> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default)
    {
        if (Column.Type == PhysicalType.Int32)
        {
            WriteConverted<int, TimeOnly, TimeMillisecondsConverter>(values, definitionLevels, repetitionLevels);
        }
        else
        {
            WriteConverted<long, TimeOnly, TimeConverter>(values, definitionLevels, repetitionLevels);
        }
    }

    /// <summary>Writes a TIMESTAMP column in any unit; see the reading overload for how kinds are treated.</summary>
    public void WriteBatch(ReadOnlySpan<DateTime> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default) =>
        WriteConverted<long, DateTime, DateTimeConverter>(values, definitionLevels, repetitionLevels);

    /// <summary>Writes a TIMESTAMP column in any unit from values converted to UTC.</summary>
    public void WriteBatch(ReadOnlySpan<DateTimeOffset> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default) =>
        WriteConverted<long, DateTimeOffset, DateTimeOffsetConverter>(values, definitionLevels, repetitionLevels);

    /// <summary>Writes a UUID column.</summary>
    public void WriteBatch(ReadOnlySpan<Guid> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default) =>
        WriteConvertedFixed<Guid, GuidConverter>(values, definitionLevels, repetitionLevels);

    /// <summary>Writes a FLOAT16 column.</summary>
    public void WriteBatch(ReadOnlySpan<Half> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default) =>
        WriteConvertedFixed<Half, HalfConverter>(values, definitionLevels, repetitionLevels);

    private void WriteConverted<TPhysical, TValue, TConverter>(ReadOnlySpan<TValue> values, ReadOnlySpan<int> definitionLevels, ReadOnlySpan<int> repetitionLevels)
        where TPhysical : unmanaged
        where TConverter : struct, IValueConverter<TPhysical, TValue, TConverter>
    {
        var converter = TConverter.Create(Column);
        var scratch = ArrayPool<TPhysical>.Shared.Rent(Math.Max(values.Length, 1));
        try
        {
            var physical = scratch.AsSpan(0, values.Length);
            for (int i = 0; i < values.Length; i++)
            {
                physical[i] = converter.ToPhysical(values[i]);
            }

            if (Column.Type == PhysicalType.Int32)
            {
                WriteBatch(MemoryMarshal.Cast<TPhysical, int>(physical), definitionLevels, repetitionLevels);
            }
            else
            {
                WriteBatch(MemoryMarshal.Cast<TPhysical, long>(physical), definitionLevels, repetitionLevels);
            }
        }
        finally
        {
            ArrayPool<TPhysical>.Shared.Return(scratch);
        }
    }

    private void WriteConvertedFixed<TValue, TConverter>(ReadOnlySpan<TValue> values, ReadOnlySpan<int> definitionLevels, ReadOnlySpan<int> repetitionLevels)
        where TConverter : struct, IFixedLengthConverter<TValue, TConverter>
    {
        var converter = TConverter.Create(Column);
        int width = converter.Width;
        var scratch = ArrayPool<byte>.Shared.Rent(Math.Max(values.Length * width, 1));
        try
        {
            for (int i = 0; i < values.Length; i++)
            {
                converter.ToBytes(values[i], scratch.AsSpan(i * width, width));
            }

            WriteFixedLengthByteArrays(scratch.AsSpan(0, values.Length * width), definitionLevels, repetitionLevels);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }
}
