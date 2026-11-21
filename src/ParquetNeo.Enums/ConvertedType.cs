namespace ParquetNeo;

/// <summary>Legacy type annotations, superseded by <c>LogicalType</c>.</summary>
public enum ConvertedType
{
    /// <summary>A UTF-8 string, in a <see cref="PhysicalType.ByteArray"/>.</summary>
    Utf8 = 0,
    /// <summary>A map, a group of a repeated group of a key and a value.</summary>
    Map = 1,
    /// <summary>The repeated group of the entries of a map in files of old writers.</summary>
    MapKeyValue = 2,
    /// <summary>A list, a group of a repeated element.</summary>
    List = 3,
    /// <summary>An enumeration, as UTF-8 text.</summary>
    Enum = 4,
    /// <summary>A decimal number with a precision and a scale.</summary>
    Decimal = 5,
    /// <summary>A date, as the days since 1970-01-01 in an <see cref="PhysicalType.Int32"/>.</summary>
    Date = 6,
    /// <summary>A time of day in milliseconds, in an <see cref="PhysicalType.Int32"/>.</summary>
    TimeMillis = 7,
    /// <summary>A time of day in microseconds, in an <see cref="PhysicalType.Int64"/>.</summary>
    TimeMicros = 8,
    /// <summary>A moment in milliseconds since the Unix epoch, in an <see cref="PhysicalType.Int64"/>.</summary>
    TimestampMillis = 9,
    /// <summary>A moment in microseconds since the Unix epoch, in an <see cref="PhysicalType.Int64"/>.</summary>
    TimestampMicros = 10,
    /// <summary>An unsigned 8 bit integer, in an <see cref="PhysicalType.Int32"/>.</summary>
    UInt8 = 11,
    /// <summary>An unsigned 16 bit integer, in an <see cref="PhysicalType.Int32"/>.</summary>
    UInt16 = 12,
    /// <summary>An unsigned 32 bit integer, in an <see cref="PhysicalType.Int32"/>.</summary>
    UInt32 = 13,
    /// <summary>An unsigned 64 bit integer, in an <see cref="PhysicalType.Int64"/>.</summary>
    UInt64 = 14,
    /// <summary>A signed 8 bit integer, in an <see cref="PhysicalType.Int32"/>.</summary>
    Int8 = 15,
    /// <summary>A signed 16 bit integer, in an <see cref="PhysicalType.Int32"/>.</summary>
    Int16 = 16,
    /// <summary>A signed 32 bit integer, in an <see cref="PhysicalType.Int32"/>.</summary>
    Int32 = 17,
    /// <summary>A signed 64 bit integer, in an <see cref="PhysicalType.Int64"/>.</summary>
    Int64 = 18,
    /// <summary>A JSON document, as UTF-8 text.</summary>
    Json = 19,
    /// <summary>A BSON document, in a <see cref="PhysicalType.ByteArray"/>.</summary>
    Bson = 20,
    /// <summary>A span of time as months, days and milliseconds, in 12 bytes of a <see cref="PhysicalType.FixedLenByteArray"/>.</summary>
    Interval = 21,
}