namespace ParquetNeo;

/// <summary>The layout of the data pages a writer produces.</summary>
public enum DataPageVersion
{
    /// <summary>The original layout, readable by every reader: the whole page body, levels and values, is compressed together.</summary>
    V1 = 1,

    /// <summary>
    /// The newer layout: the levels are stored uncompressed in front of the values and only the values are compressed, and the
    /// page header says how many rows and nulls the page holds. Pages also start at row boundaries, which readers can use to
    /// skip or parallelize. Some older readers do not support it.
    /// </summary>
    V2 = 2,
}
