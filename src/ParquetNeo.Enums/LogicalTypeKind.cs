namespace ParquetNeo;

/// <summary>Kind of a <c>LogicalType</c>. Values are the field ids of the thrift union.</summary>
public enum LogicalTypeKind
{
    /// <summary>A union member this library does not know; callers should fall back to the physical type.</summary>
    Unrecognized = 0,
    /// <summary>A UTF-8 string.</summary>
    String = 1,
    /// <summary>A map.</summary>
    Map = 2,
    /// <summary>A list.</summary>
    List = 3,
    /// <summary>An enumeration, as UTF-8 text.</summary>
    Enum = 4,
    /// <summary>A decimal number with a precision and a scale.</summary>
    Decimal = 5,
    /// <summary>A date, as the days since 1970-01-01.</summary>
    Date = 6,
    /// <summary>A time of day, with a unit and whether it is in UTC.</summary>
    Time = 7,
    /// <summary>A moment, with a unit and whether it is in UTC.</summary>
    Timestamp = 8,
    /// <summary>An integer of 8, 16, 32 or 64 bits, signed or not.</summary>
    Integer = 10,
    /// <summary>A column that only contains nulls (thrift <c>UNKNOWN</c> / <c>NullType</c>).</summary>
    Null = 11,
    /// <summary>A JSON document, as UTF-8 text.</summary>
    Json = 12,
    /// <summary>A BSON document.</summary>
    Bson = 13,
    /// <summary>A UUID, in 16 bytes.</summary>
    Uuid = 14,
    /// <summary>A half precision floating point number, in 2 bytes.</summary>
    Float16 = 15,
}