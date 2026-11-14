namespace ParquetNeo.Storage;

/// <summary>How a <see cref="RangeReadStream"/> reads: in blocks of what size, and how many it keeps.</summary>
public sealed record RangeReadStreamOptions
{
    /// <summary>
    /// A read smaller than this takes the block it is in from the storage and keeps it, so that the many small reads of a footer or of
    /// a page header are one request; a read as large as this or larger is a request of its own, for exactly what it asks. 1 MiB when not set.
    /// </summary>
    public int BlockSize { get; init; } = 1024 * 1024;

    /// <summary>How many blocks are kept, the ones read last. 4 when not set.</summary>
    public int CachedBlocks { get; init; } = 4;

    /// <summary>
    /// The first read of the last bytes of the object, which is how the footer of a Parquet file is found (its size is in the last 8 bytes, and
    /// the footer is before them), takes this many bytes of the end in one request and keeps them, so that finding the footer is one round trip
    /// and not three. 64 KiB when not set; 0 turns it off. An object that is not longer than this is read whole at the first read of its end.
    /// </summary>
    public int TailSize { get; init; } = 64 * 1024;
}
