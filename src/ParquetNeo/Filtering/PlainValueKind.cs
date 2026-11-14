namespace ParquetNeo.Filtering;

/// <summary>What a value of a filter is as the plain encoding of a column, which a bloom filter looks values up by.</summary>
internal enum PlainValueKind
{
    /// <summary>The value has no plain encoding to look up: bloom filters say nothing of it.</summary>
    Unsupported,

    /// <summary>No value of the column reads back as this one, so no row has it.</summary>
    NotStorable,

    /// <summary>The bytes the value is written as, if it is there.</summary>
    Bytes,
}
