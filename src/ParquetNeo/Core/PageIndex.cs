using ParquetNeo.Metadata;

namespace ParquetNeo.Core;

/// <summary>The page index of a column chunk, to be written after the row groups of the file.</summary>
internal sealed class PageIndex
{
    public PageIndex(OffsetIndex offsets, ColumnIndex? columns)
    {
        Offsets = offsets;
        Columns = columns;
    }

    public OffsetIndex Offsets { get; }

    /// <summary>The bounds of the pages; null when the chunk cannot have them (a column whose values have no order, or a page of only NaN).</summary>
    public ColumnIndex? Columns { get; }

    /// <summary>Moves the positions of the pages by <paramref name="shift"/>, for a chunk that was written to a buffer and then copied to the file.</summary>
    public void Shift(long shift)
    {
        foreach (var location in Offsets.PageLocations)
        {
            location.Offset += shift;
        }
    }
}
