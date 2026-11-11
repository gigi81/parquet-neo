namespace ParquetNeo;

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