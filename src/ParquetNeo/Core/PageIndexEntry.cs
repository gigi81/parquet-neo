namespace ParquetNeo.Core;

// A namespace called Statistics would otherwise hide the Statistics type of the metadata.
using Statistics = Metadata.Statistics;

/// <summary>What the page index records about one data page while its column chunk is written: where the page is and the bounds of its values.</summary>
internal sealed class PageIndexEntry
{
    /// <summary>Where the page starts in the sink (a buffer, until the chunk is copied to the file).</summary>
    public long Offset { get; set; }

    /// <summary>The size of the page with its header.</summary>
    public int Size { get; set; }

    /// <summary>The row of the row group the page starts with.</summary>
    public long FirstRow { get; set; }

    /// <summary>How many values of the page are not null.</summary>
    public long ValueCount { get; set; }

    public long NullCount { get; set; }

    /// <summary>The bounds of the values of the page; null when the column keeps no statistics.</summary>
    public Statistics? Statistics { get; set; }
}
