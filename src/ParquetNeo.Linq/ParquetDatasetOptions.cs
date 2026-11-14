using ParquetNeo.Serialization;

namespace ParquetNeo.Linq;

/// <summary>Settings for a <see cref="ParquetDataset{T}"/>.</summary>
public sealed record ParquetDatasetOptions
{
    /// <summary>Which files of a directory are part of the dataset: a search pattern for <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/>. Defaults to <c>*.parquet</c>.</summary>
    public string SearchPattern { get; init; } = "*.parquet";

    /// <summary>Whether the folders below a directory are searched as well, as folders of partitions are. Defaults to <c>true</c>.</summary>
    public bool Recursive { get; init; } = true;

    /// <summary>
    /// How many files are read at the same time. The rows of a query come in the order the files are read, which with more than one
    /// is not the order of their names; use <c>OrderBy</c> when the order matters. Defaults to the number of processors; 1 reads the
    /// files one after the other, in the order of their names.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;

    /// <summary>The codecs and limits the files are read with. Its <see cref="ParquetDeserializerOptions.RowGroupFilter"/> is left to the query.</summary>
    public ParquetDeserializerOptions Deserializer { get; init; } = new();
}
