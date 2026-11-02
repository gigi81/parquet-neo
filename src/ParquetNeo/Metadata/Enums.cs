namespace ParquetNeo.Metadata;

// Values match parquet.thrift. Unknown values read from a file are preserved as-is.

/// <summary>Physical (storage) type of a column (thrift <c>Type</c>).</summary>
public enum PhysicalType
{
    Boolean = 0,
    Int32 = 1,
    Int64 = 2,
    /// <summary>Deprecated, kept for reading legacy files.</summary>
    Int96 = 3,
    Float = 4,
    Double = 5,
    ByteArray = 6,
    FixedLenByteArray = 7,
}

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

public enum FieldRepetitionType
{
    Required = 0,
    Optional = 1,
    Repeated = 2,
}

/// <summary>Value encodings (thrift <c>Encoding</c>).</summary>
public enum ParquetEncoding
{
    Plain = 0,
    /// <summary>Deprecated; <see cref="RleDictionary"/> is used instead.</summary>
    PlainDictionary = 2,
    Rle = 3,
    /// <summary>Deprecated; superseded by <see cref="Rle"/>.</summary>
    BitPacked = 4,
    DeltaBinaryPacked = 5,
    DeltaLengthByteArray = 6,
    DeltaByteArray = 7,
    RleDictionary = 8,
    ByteStreamSplit = 9,
    Alp = 10,
}

public enum CompressionCodec
{
    Uncompressed = 0,
    Snappy = 1,
    Gzip = 2,
    Lzo = 3,
    Brotli = 4,
    /// <summary>Deprecated Hadoop-framed LZ4; use <see cref="Lz4Raw"/>.</summary>
    Lz4 = 5,
    Zstd = 6,
    Lz4Raw = 7,
}

public enum PageType
{
    DataPage = 0,
    IndexPage = 1,
    DictionaryPage = 2,
    DataPageV2 = 3,
}

/// <summary>Order of a column's type-defined sort (thrift <c>ColumnOrder</c> union).</summary>
public enum ColumnOrder
{
    /// <summary>A union member this library does not know.</summary>
    Unrecognized = 0,
    TypeDefined = 1,
    Ieee754TotalOrder = 2,
    Int96Timestamp = 3,
}

public enum TimeUnit
{
    Millis = 1,
    Micros = 2,
    Nanos = 3,
}
