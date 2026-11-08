using ParquetNeo.ColumnTypes;

namespace ParquetNeo.Core;

/// <summary>
/// Writing columns with a logical type from the .NET type it stands for; the counterpart of the logical overloads of
/// <see cref="ColumnReader"/>. Each overload is the <see cref="ColumnType{TValue}"/> of that type writing the column, which checks
/// the column's logical type and throws <see cref="ArgumentException"/> for a column of another type. Levels work as in the
/// physical overloads. <see cref="WriteBatch{TValue}"/> writes with any column type, including your own.
/// </summary>
public sealed partial class ColumnWriter
{
    /// <summary>Writes a batch with <paramref name="type"/>, which converts <typeparamref name="TValue"/> to the column's values.</summary>
    /// <exception cref="ArgumentException">The column does not fit the type.</exception>
    public void WriteBatch<TValue>(ColumnType<TValue> type, ReadOnlySpan<TValue> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default)
    {
        ArgumentNullException.ThrowIfNull(type);
        type.Write(this, values, definitionLevels, repetitionLevels);
    }

    /// <summary>Writes an INT_8 column.</summary>
    public void WriteBatch(ReadOnlySpan<sbyte> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default) =>
        ColumnTypeRegistry.Get<sbyte>().Write(this, values, definitionLevels, repetitionLevels);

    /// <summary>Writes a UINT_8 column.</summary>
    public void WriteBatch(ReadOnlySpan<byte> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default) =>
        ColumnTypeRegistry.Get<byte>().Write(this, values, definitionLevels, repetitionLevels);

    /// <summary>Writes an INT_16 column.</summary>
    public void WriteBatch(ReadOnlySpan<short> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default) =>
        ColumnTypeRegistry.Get<short>().Write(this, values, definitionLevels, repetitionLevels);

    /// <summary>Writes a UINT_16 column.</summary>
    public void WriteBatch(ReadOnlySpan<ushort> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default) =>
        ColumnTypeRegistry.Get<ushort>().Write(this, values, definitionLevels, repetitionLevels);

    /// <summary>Writes a UINT_32 column.</summary>
    public void WriteBatch(ReadOnlySpan<uint> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default) =>
        ColumnTypeRegistry.Get<uint>().Write(this, values, definitionLevels, repetitionLevels);

    /// <summary>Writes a UINT_64 column.</summary>
    public void WriteBatch(ReadOnlySpan<ulong> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default) =>
        ColumnTypeRegistry.Get<ulong>().Write(this, values, definitionLevels, repetitionLevels);

    /// <summary>Writes a DATE column.</summary>
    public void WriteBatch(ReadOnlySpan<DateOnly> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default) =>
        ColumnTypeRegistry.Get<DateOnly>().Write(this, values, definitionLevels, repetitionLevels);

    /// <summary>Writes a TIME column in any unit; times are cut to the unit.</summary>
    public void WriteBatch(ReadOnlySpan<TimeOnly> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default) =>
        ColumnTypeRegistry.Get<TimeOnly>().Write(this, values, definitionLevels, repetitionLevels);

    /// <summary>Writes a TIMESTAMP column in any unit; see the reading overload for how kinds are treated.</summary>
    public void WriteBatch(ReadOnlySpan<DateTime> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default) =>
        ColumnTypeRegistry.Get<DateTime>().Write(this, values, definitionLevels, repetitionLevels);

    /// <summary>Writes a TIMESTAMP column in any unit from values converted to UTC.</summary>
    public void WriteBatch(ReadOnlySpan<DateTimeOffset> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default) =>
        ColumnTypeRegistry.Get<DateTimeOffset>().Write(this, values, definitionLevels, repetitionLevels);

    /// <summary>Writes a UUID column.</summary>
    public void WriteBatch(ReadOnlySpan<Guid> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default) =>
        ColumnTypeRegistry.Get<Guid>().Write(this, values, definitionLevels, repetitionLevels);

    /// <summary>Writes a FLOAT16 column.</summary>
    public void WriteBatch(ReadOnlySpan<Half> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default) =>
        ColumnTypeRegistry.Get<Half>().Write(this, values, definitionLevels, repetitionLevels);

    /// <summary>Writes a DECIMAL column of any physical type (INT32, INT64, FIXED_LEN_BYTE_ARRAY or BYTE_ARRAY) from <see cref="decimal"/>
    /// values. Digits are never lost: a value with more decimals than the column's scale, or more digits than its precision,
    /// is an <see cref="ArgumentOutOfRangeException"/>. A column with a scale above 28 cannot be written from a <see cref="decimal"/>.</summary>
    public void WriteBatch(ReadOnlySpan<decimal> values, ReadOnlySpan<int> definitionLevels = default, ReadOnlySpan<int> repetitionLevels = default) =>
        ColumnTypeRegistry.Get<decimal>().Write(this, values, definitionLevels, repetitionLevels);
}
