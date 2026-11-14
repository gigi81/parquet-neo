namespace ParquetNeo;

/// <summary>A run of rows of a row group: the position of the first and how many.</summary>
public readonly record struct RowRange(long Start, long Count)
{
    /// <summary>The position after the last row.</summary>
    public long End => Start + Count;
}
