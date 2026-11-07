namespace ParquetNeo.Core;

/// <summary>How the values of a BYTE_ARRAY or FIXED_LEN_BYTE_ARRAY column compare.</summary>
internal enum BinaryOrder
{
    /// <summary>Unsigned byte-wise lexicographic order: strings, enums, JSON, BSON, UUIDs and plain binary.</summary>
    UnsignedBytes,

    /// <summary>Two's complement big-endian numbers, as used for decimals; shorter values are sign extended.</summary>
    SignedBigEndian,
}
