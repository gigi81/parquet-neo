using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using ParquetNeo.Converters;
using ParquetNeo.Encodings;
using ParquetNeo.Exceptions;
using ParquetNeo.Metadata;

namespace ParquetNeo.Core;

/// <summary>
/// Reading columns with a logical type as the .NET type it stands for. Each overload checks the column's logical type, so
/// reading a DATE column as <see cref="int"/> still gives the raw days and reading it as <see cref="DateOnly"/> gives dates;
/// a column of another type is an <see cref="ArgumentException"/>. Levels work as in the physical overloads.
/// </summary>
public sealed partial class ColumnReader
{
    /// <summary>Reads an INT_8 column.</summary>
    public BatchRead ReadBatch(Span<sbyte> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default) =>
        ReadConverted<int, sbyte, SByteConverter>(values, definitionLevels, repetitionLevels);

    /// <summary>Reads a UINT_8 column.</summary>
    public BatchRead ReadBatch(Span<byte> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default) =>
        ReadConverted<int, byte, ByteConverter>(values, definitionLevels, repetitionLevels);

    /// <summary>Reads an INT_16 column.</summary>
    public BatchRead ReadBatch(Span<short> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default) =>
        ReadConverted<int, short, Int16Converter>(values, definitionLevels, repetitionLevels);

    /// <summary>Reads a UINT_16 column.</summary>
    public BatchRead ReadBatch(Span<ushort> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default) =>
        ReadConverted<int, ushort, UInt16Converter>(values, definitionLevels, repetitionLevels);

    /// <summary>Reads a UINT_32 column.</summary>
    public BatchRead ReadBatch(Span<uint> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default) =>
        ReadConverted<int, uint, UInt32Converter>(values, definitionLevels, repetitionLevels);

    /// <summary>Reads a UINT_64 column.</summary>
    public BatchRead ReadBatch(Span<ulong> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default) =>
        ReadConverted<long, ulong, UInt64Converter>(values, definitionLevels, repetitionLevels);

    /// <summary>Reads a DATE column.</summary>
    public BatchRead ReadBatch(Span<DateOnly> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default) =>
        ReadConverted<int, DateOnly, DateConverter>(values, definitionLevels, repetitionLevels);

    /// <summary>Reads a TIME column in any unit. <see cref="TimeOnly"/> counts 100 ns ticks, so nanoseconds below that are dropped.</summary>
    public BatchRead ReadBatch(Span<TimeOnly> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default) =>
        Column.Type == PhysicalType.Int32
            ? ReadConverted<int, TimeOnly, TimeMillisecondsConverter>(values, definitionLevels, repetitionLevels)
            : ReadConverted<long, TimeOnly, TimeConverter>(values, definitionLevels, repetitionLevels);

    /// <summary>
    /// Reads a TIMESTAMP column in any unit as <see cref="DateTime"/>: UTC when the column is adjusted to UTC, otherwise
    /// <see cref="DateTimeKind.Unspecified"/>. A moment outside the years 1 to 9999 is a <see cref="ValueConversionException"/>.
    /// An INT96 column, the deprecated timestamp of Impala, Hive and older Spark, is read too: it has no time zone, so its values
    /// are <see cref="DateTimeKind.Unspecified"/>. INT96 columns can be read but not written.
    /// </summary>
    public BatchRead ReadBatch(Span<DateTime> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default) =>
        Column.Type == PhysicalType.Int96
            ? ReadInt96<DateTime>(values, definitionLevels, repetitionLevels)
            : ReadConverted<long, DateTime, DateTimeConverter>(values, definitionLevels, repetitionLevels);

    /// <summary>
    /// Reads a TIMESTAMP column in any unit as <see cref="DateTimeOffset"/> at offset zero. An INT96 column, the deprecated
    /// timestamp of Impala, Hive and older Spark, reads the same way, as the clock reading it holds.
    /// </summary>
    public BatchRead ReadBatch(Span<DateTimeOffset> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default) =>
        Column.Type == PhysicalType.Int96
            ? ReadInt96<DateTimeOffset>(values, definitionLevels, repetitionLevels)
            : ReadConverted<long, DateTimeOffset, DateTimeOffsetConverter>(values, definitionLevels, repetitionLevels);

    /// <summary>Reads a UUID column.</summary>
    public BatchRead ReadBatch(Span<Guid> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default) =>
        ReadConvertedFixed<Guid, GuidConverter>(values, definitionLevels, repetitionLevels);

    /// <summary>Reads a FLOAT16 column.</summary>
    public BatchRead ReadBatch(Span<Half> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default) =>
        ReadConvertedFixed<Half, HalfConverter>(values, definitionLevels, repetitionLevels);

    /// <summary>
    /// Reads a DECIMAL column of any physical type (INT32, INT64, FIXED_LEN_BYTE_ARRAY or BYTE_ARRAY) as <see cref="decimal"/>.
    /// A <see cref="decimal"/> holds 96 bits and a scale of up to 28, so a column with a larger scale is an
    /// <see cref="ArgumentException"/> and a larger stored value is a <see cref="ValueConversionException"/>. A column
    /// of BYTE_ARRAY values may return fewer values than requested, as <see cref="ReadByteArrays"/> does.
    /// </summary>
    public BatchRead ReadBatch(Span<decimal> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default) =>
        Column.Type switch
        {
            PhysicalType.Int32 => ReadConverted<int, decimal, Decimal32Converter>(values, definitionLevels, repetitionLevels),
            PhysicalType.Int64 => ReadConverted<long, decimal, Decimal64Converter>(values, definitionLevels, repetitionLevels),
            PhysicalType.FixedLenByteArray => ReadConvertedFixed<decimal, DecimalFixedConverter>(values, definitionLevels, repetitionLevels),
            PhysicalType.ByteArray => ReadDecimalByteArrays(values, definitionLevels, repetitionLevels),
            _ => throw ConversionRules.Mismatch(Column, "decimal", "DECIMAL column stored as INT32, INT64, FIXED_LEN_BYTE_ARRAY or BYTE_ARRAY"),
        };

    /// <summary>
    /// Reads a BYTE_ARRAY column that is annotated STRING, ENUM or JSON, or not annotated, as UTF-8 text. Bytes that are not
    /// valid UTF-8 are a <see cref="ValueConversionException"/>. Like <see cref="ReadByteArrays"/>, a batch ends at the end of a
    /// page and may return fewer values than requested.
    /// </summary>
    public BatchRead ReadStrings(Span<string> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default)
    {
        ConversionRules.RequireText(Column);
        var scratch = ArrayPool<ReadOnlyMemory<byte>>.Shared.Rent(Math.Max(values.Length, 1));
        try
        {
            BatchRead read = ReadByteArrays(scratch.AsSpan(0, values.Length), definitionLevels, repetitionLevels);
            for (int i = 0; i < read.Values; i++)
            {
                try
                {
                    values[i] = StrictUtf8.GetString(scratch[i].Span);
                }
                catch (DecoderFallbackException exception)
                {
                    throw new ValueConversionException($"Column '{Column.Name}' holds text that is not valid UTF-8.", exception);
                }
            }

            return read;
        }
        finally
        {
            ArrayPool<ReadOnlyMemory<byte>>.Shared.Return(scratch, clearArray: true);
        }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private BatchRead ReadDecimalByteArrays(Span<decimal> values, Span<int> definitionLevels, Span<int> repetitionLevels)
    {
        var converter = DecimalByteArrayConverter.Create(Column);
        var scratch = ArrayPool<ReadOnlyMemory<byte>>.Shared.Rent(Math.Max(values.Length, 1));
        try
        {
            BatchRead read = ReadByteArrays(scratch.AsSpan(0, values.Length), definitionLevels, repetitionLevels);
            for (int i = 0; i < read.Values; i++)
            {
                values[i] = converter.ToValue(scratch[i].Span);
            }

            return read;
        }
        finally
        {
            ArrayPool<ReadOnlyMemory<byte>>.Shared.Return(scratch, clearArray: true);
        }
    }

    private BatchRead ReadInt96<TValue>(Span<TValue> values, Span<int> definitionLevels, Span<int> repetitionLevels)
    {
        var scratch = ArrayPool<Int96>.Shared.Rent(Math.Max(values.Length, 1));
        try
        {
            BatchRead read = ReadFixedWidth<Int96, Int96Decoder>(PhysicalType.Int96, scratch.AsSpan(0, values.Length), definitionLevels, repetitionLevels);
            for (int i = 0; i < read.Values; i++)
            {
                long ticks = Int96Timestamp.ToTicks(scratch[i]);
                if (typeof(TValue) == typeof(DateTime))
                {
                    values[i] = (TValue)(object)new DateTime(ticks, DateTimeKind.Unspecified);
                }
                else
                {
                    values[i] = (TValue)(object)new DateTimeOffset(ticks, TimeSpan.Zero);
                }
            }

            return read;
        }
        finally
        {
            ArrayPool<Int96>.Shared.Return(scratch);
        }
    }

    private BatchRead ReadConverted<TPhysical, TValue, TConverter>(Span<TValue> values, Span<int> definitionLevels, Span<int> repetitionLevels)
        where TPhysical : unmanaged
        where TConverter : struct, IValueConverter<TPhysical, TValue, TConverter>
    {
        var converter = TConverter.Create(Column);
        var scratch = ArrayPool<TPhysical>.Shared.Rent(Math.Max(values.Length, 1));
        try
        {
            var physical = scratch.AsSpan(0, values.Length);
            BatchRead read = Column.Type == PhysicalType.Int32
                ? ReadBatch(MemoryMarshal.Cast<TPhysical, int>(physical), definitionLevels, repetitionLevels)
                : ReadBatch(MemoryMarshal.Cast<TPhysical, long>(physical), definitionLevels, repetitionLevels);
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

    private BatchRead ReadConvertedFixed<TValue, TConverter>(Span<TValue> values, Span<int> definitionLevels, Span<int> repetitionLevels)
        where TConverter : struct, IFixedLengthConverter<TValue, TConverter>
    {
        var converter = TConverter.Create(Column);
        int width = converter.Width;
        var scratch = ArrayPool<byte>.Shared.Rent(Math.Max(values.Length * width, 1));
        try
        {
            BatchRead read = ReadFixedLengthByteArrays(scratch.AsSpan(0, values.Length * width), definitionLevels, repetitionLevels);
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
}
