using System.Diagnostics.CodeAnalysis;

namespace ParquetNeo.Linq;

/// <summary>Opens datasets: files of rows of one type, to be queried with LINQ.</summary>
public static class ParquetDataset
{
    /// <summary>
    /// A query over the rows in a Parquet file, or in all the Parquet files of a directory (and the folders below it). Nothing is
    /// read until the query is run, and then as little as the query allows: files and row groups whose statistics rule out every row
    /// that a <c>Where</c> asks for are not read, and only the columns the query uses are (when it ends in a <c>Select</c>).
    /// </summary>
    /// <typeparam name="T">The type of a row, which the serializer reads: its properties are matched to the columns by name.</typeparam>
    /// <param name="path">A file, or a directory of files that all have the columns of <typeparamref name="T"/>.</param>
    [RequiresUnreferencedCode("Queries are built from expression trees, and types of the columns they use are made at run time.")]
    [RequiresDynamicCode("Queries are built from expression trees, and types of the columns they use are made at run time.")]
    public static ParquetDataset<T> Open<T>(string path, ParquetDatasetOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new ParquetDataset<T>(new DatasetSource(path, options ?? new ParquetDatasetOptions()));
    }
}
