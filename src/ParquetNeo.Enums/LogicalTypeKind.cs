namespace ParquetNeo;

/// <summary>Kind of a <c>LogicalType</c>. Values are the field ids of the thrift union.</summary>
public enum LogicalTypeKind
{
    /// <summary>A union member this library does not know; callers should fall back to the physical type.</summary>
    Unrecognized = 0,
    String = 1,
    Map = 2,
    List = 3,
    Enum = 4,
    Decimal = 5,
    Date = 6,
    Time = 7,
    Timestamp = 8,
    Integer = 10,
    /// <summary>A column that only contains nulls (thrift <c>UNKNOWN</c> / <c>NullType</c>).</summary>
    Null = 11,
    Json = 12,
    Bson = 13,
    Uuid = 14,
    Float16 = 15,
}