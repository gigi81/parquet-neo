using System.Diagnostics.CodeAnalysis;

using ParquetNeo.Storage;

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

    /// <summary>
    /// A query over the Parquet files of a storage (a bucket of Amazon S3, a container of Azure Blob Storage, a folder of a disk) whose keys start with
    /// <paramref name="prefix"/>, with the folders below it. It skips files and row groups, and reads only the columns a query uses, as the
    /// other overload does, and from the storage only the parts of the files that it reads.
    /// </summary>
    /// <typeparam name="T">The type of a row, which the serializer reads: its properties are matched to the columns by name.</typeparam>
    /// <param name="storage">Where the files are, for example an <c>S3ParquetStorage</c> or an <c>AzureBlobParquetStorage</c>.</param>
    /// <param name="prefix">The start of the keys of the files, such as <c>people/2024/</c>; empty for every file of the storage.</param>
    [RequiresUnreferencedCode("Queries are built from expression trees, and types of the columns they use are made at run time.")]
    [RequiresDynamicCode("Queries are built from expression trees, and types of the columns they use are made at run time.")]
    public static ParquetDataset<T> Open<T>(IParquetStorage storage, string prefix = "", ParquetDatasetOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(prefix);
        return new ParquetDataset<T>(new DatasetSource(prefix, options ?? new ParquetDatasetOptions(), storage));
    }
}
