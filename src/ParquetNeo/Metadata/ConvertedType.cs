namespace ParquetNeo.Metadata;

/// <summary>Legacy type annotations, superseded by <see cref="LogicalType"/>.</summary>
public enum ConvertedType
{
    Utf8 = 0,
    Map = 1,
    MapKeyValue = 2,
    List = 3,
    Enum = 4,
    Decimal = 5,
    Date = 6,
    TimeMillis = 7,
    TimeMicros = 8,
    TimestampMillis = 9,
    TimestampMicros = 10,
    UInt8 = 11,
    UInt16 = 12,
    UInt32 = 13,
    UInt64 = 14,
    Int8 = 15,
    Int16 = 16,
    Int32 = 17,
    Int64 = 18,
    Json = 19,
    Bson = 20,
    Interval = 21,
}