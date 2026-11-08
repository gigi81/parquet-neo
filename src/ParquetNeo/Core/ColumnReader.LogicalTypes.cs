using System.Buffers;
using ParquetNeo.ColumnTypes;
using ParquetNeo.Encodings;
using ParquetNeo.Exceptions;
using ParquetNeo.Metadata;

namespace ParquetNeo.Core;

/// <summary>
/// Reading columns with a logical type as the .NET type it stands for. Each overload is the <see cref="ColumnType{TValue}"/> of
/// that type reading the column, which checks the column's logical type: reading a DATE column as <see cref="int"/> still gives
/// the raw days and reading it as <see cref="DateOnly"/> gives dates; a column of another type is an <see cref="ArgumentException"/>.
/// Levels work as in the physical overloads. <see cref="ReadBatch{TValue}"/> reads with any column type, including your own.
/// </summary>
public sealed partial class ColumnReader
{
    /// <summary>Reads a batch with <paramref name="type"/>, which converts the column's values to <typeparamref name="TValue"/>.</summary>
    /// <exception cref="ArgumentException">The column does not fit the type.</exception>
    public BatchRead ReadBatch<TValue>(ColumnType<TValue> type, Span<TValue> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.Read(this, values, definitionLevels, repetitionLevels);
    }

    /// <summary>Reads an INT_8 column.</summary>
    public BatchRead ReadBatch(Span<sbyte> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default) =>
        ColumnTypeRegistry.Get<sbyte>().Read(this, values, definitionLevels, repetitionLevels);

    /// <summary>Reads a UINT_8 column.</summary>
    public BatchRead ReadBatch(Span<byte> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default) =>
        ColumnTypeRegistry.Get<byte>().Read(this, values, definitionLevels, repetitionLevels);

    /// <summary>Reads an INT_16 column.</summary>
    public BatchRead ReadBatch(Span<short> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default) =>
        ColumnTypeRegistry.Get<short>().Read(this, values, definitionLevels, repetitionLevels);

    /// <summary>Reads a UINT_16 column.</summary>
    public BatchRead ReadBatch(Span<ushort> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default) =>
        ColumnTypeRegistry.Get<ushort>().Read(this, values, definitionLevels, repetitionLevels);

    /// <summary>Reads a UINT_32 column.</summary>
    public BatchRead ReadBatch(Span<uint> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default) =>
        ColumnTypeRegistry.Get<uint>().Read(this, values, definitionLevels, repetitionLevels);

    /// <summary>Reads a UINT_64 column.</summary>
    public BatchRead ReadBatch(Span<ulong> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default) =>
        ColumnTypeRegistry.Get<ulong>().Read(this, values, definitionLevels, repetitionLevels);

    /// <summary>Reads a DATE column.</summary>
    public BatchRead ReadBatch(Span<DateOnly> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default) =>
        ColumnTypeRegistry.Get<DateOnly>().Read(this, values, definitionLevels, repetitionLevels);

    /// <summary>Reads a TIME column in any unit. <see cref="TimeOnly"/> counts 100 ns ticks, so nanoseconds below that are dropped.</summary>
    public BatchRead ReadBatch(Span<TimeOnly> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default) =>
        ColumnTypeRegistry.Get<TimeOnly>().Read(this, values, definitionLevels, repetitionLevels);

    /// <summary>Reads a TIMESTAMP column in any unit as <see cref="DateTime"/>: UTC when the column is adjusted to UTC, otherwise
    /// <see cref="DateTimeKind.Unspecified"/>. A moment outside the years 1 to 9999 is a <see cref="ValueConversionException"/>.
    /// An INT96 column, the deprecated timestamp of Impala, Hive and older Spark, is read too: it has no time zone, so its values
    /// are <see cref="DateTimeKind.Unspecified"/>. INT96 columns can be read but not written.</summary>
    public BatchRead ReadBatch(Span<DateTime> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default) =>
        ColumnTypeRegistry.Get<DateTime>().Read(this, values, definitionLevels, repetitionLevels);

    /// <summary>Reads a TIMESTAMP column in any unit as <see cref="DateTimeOffset"/> at offset zero. An INT96 column, the deprecated
    /// timestamp of Impala, Hive and older Spark, reads the same way, as the clock reading it holds.</summary>
    public BatchRead ReadBatch(Span<DateTimeOffset> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default) =>
        ColumnTypeRegistry.Get<DateTimeOffset>().Read(this, values, definitionLevels, repetitionLevels);

    /// <summary>Reads a UUID column.</summary>
    public BatchRead ReadBatch(Span<Guid> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default) =>
        ColumnTypeRegistry.Get<Guid>().Read(this, values, definitionLevels, repetitionLevels);

    /// <summary>Reads a FLOAT16 column.</summary>
    public BatchRead ReadBatch(Span<Half> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default) =>
        ColumnTypeRegistry.Get<Half>().Read(this, values, definitionLevels, repetitionLevels);

    /// <summary>Reads a DECIMAL column of any physical type (INT32, INT64, FIXED_LEN_BYTE_ARRAY or BYTE_ARRAY) as <see cref="decimal"/>.
    /// A <see cref="decimal"/> holds 96 bits and a scale of up to 28, so a column with a larger scale is an
    /// <see cref="ArgumentException"/> and a larger stored value is a <see cref="ValueConversionException"/>. A column
    /// of BYTE_ARRAY values may return fewer values than requested, as <see cref="ReadByteArrays"/> does.</summary>
    public BatchRead ReadBatch(Span<decimal> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default) =>
        ColumnTypeRegistry.Get<decimal>().Read(this, values, definitionLevels, repetitionLevels);

    /// <summary>
    /// Reads a BYTE_ARRAY column that is annotated STRING, ENUM or JSON, or not annotated, as UTF-8 text. Bytes that are not
    /// valid UTF-8 are a <see cref="ValueConversionException"/>. Like <see cref="ReadByteArrays"/>, a batch ends at the end of a
    /// page and may return fewer values than requested.
    /// </summary>
    public BatchRead ReadStrings(Span<string> values, Span<int> definitionLevels = default, Span<int> repetitionLevels = default) =>
        ColumnTypeRegistry.Get<string>().Read(this, values, definitionLevels, repetitionLevels);

    /// <summary>Reads the ticks of an INT96 timestamp column, for the column types that give them as a date and time.</summary>
    internal BatchRead ReadInt96Ticks(Span<long> ticks, Span<int> definitionLevels, Span<int> repetitionLevels)
    {
        var scratch = ArrayPool<Int96>.Shared.Rent(Math.Max(ticks.Length, 1));
        try
        {
            BatchRead read = ReadFixedWidth<Int96, Int96Decoder>(PhysicalType.Int96, scratch.AsSpan(0, ticks.Length), definitionLevels, repetitionLevels);
            for (int i = 0; i < read.Values; i++)
            {
                ticks[i] = Int96Timestamp.ToTicks(scratch[i]);
            }

            return read;
        }
        finally
        {
            ArrayPool<Int96>.Shared.Return(scratch);
        }
    }
}
