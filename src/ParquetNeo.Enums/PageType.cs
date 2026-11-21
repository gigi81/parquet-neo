namespace ParquetNeo;

/// <summary>The kind of a page of a column chunk (thrift <c>PageType</c>).</summary>
public enum PageType
{
    /// <summary>A data page of version 1.</summary>
    DataPage = 0,
    /// <summary>An index page, which this library neither writes nor reads.</summary>
    IndexPage = 1,
    /// <summary>The dictionary of a dictionary encoded column chunk.</summary>
    DictionaryPage = 2,
    /// <summary>A data page of version 2, whose levels are not compressed.</summary>
    DataPageV2 = 3,
}