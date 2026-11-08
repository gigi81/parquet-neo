using ParquetNeo.Core;

namespace ParquetNeo.Serialization.Columns;

/// <summary>The entries of one column of a row group, read into memory so that rows can be assembled from several columns at once.</summary>
internal interface ILeafColumn
{
    /// <summary>Reads all the entries of the column chunk, or stands in for a column the file does not have when <paramref name="reader"/> is null.</summary>
    void Fill(ColumnReader? reader, long rows);
}
