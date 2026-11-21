namespace ParquetNeo;

// Values match parquet.thrift. Unknown values read from a file are preserved as-is.

/// <summary>Physical (storage) type of a column (thrift <c>Type</c>).</summary>
public enum PhysicalType
{
    /// <summary>One bit.</summary>
    Boolean = 0,
    /// <summary>A signed 32 bit integer.</summary>
    Int32 = 1,
    /// <summary>A signed 64 bit integer.</summary>
    Int64 = 2,
    /// <summary>Deprecated, kept for reading legacy files.</summary>
    Int96 = 3,
    /// <summary>A 32 bit IEEE 754 floating point number.</summary>
    Float = 4,
    /// <summary>A 64 bit IEEE 754 floating point number.</summary>
    Double = 5,
    /// <summary>A sequence of bytes of any length.</summary>
    ByteArray = 6,
    /// <summary>A sequence of bytes of a length that is the same for every value.</summary>
    FixedLenByteArray = 7,
}