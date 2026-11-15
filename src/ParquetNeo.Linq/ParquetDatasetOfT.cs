using System.Collections;
using System.Linq.Expressions;

using ParquetNeo.Storage;

namespace ParquetNeo.Linq;

/// <summary>The rows of a file or of the files of a directory, as an <see cref="IQueryable{T}"/>; see <see cref="ParquetDataset.Open{T}(string, ParquetDatasetOptions?)"/>.</summary>
public sealed class ParquetDataset<T> : IOrderedQueryable<T>, IDatasetRoot
{
    internal ParquetDataset(DatasetSource source)
    {
        Source = source;
        Provider = new ParquetQueryProvider(source);
        Expression = Expression.Constant(this);
    }

    /// <summary>The file or directory the rows are in, or the prefix of the keys of their files in a storage.</summary>
    public string Path => Source.Path;

    /// <summary>
    /// What the queries of this dataset have read so far: how many files were opened, how many requests were made to the storage and how many bytes
    /// they asked for. The counts add up over the queries, and are for the whole dataset, so to measure one query call <see cref="ReadStatistics.Reset"/>
    /// before it and read them after, with no other query of the dataset running. <see cref="ParquetQueryableExtensions.Explain{T}(IQueryable{T})"/> reads the footers of the
    /// files, which counts too. For the files of a disk a request is a read of the file.
    /// </summary>
    public ReadStatistics ReadStatistics => Source.Statistics;

    public Type ElementType => typeof(T);

    public Expression Expression { get; }

    public IQueryProvider Provider { get; }

    internal DatasetSource Source { get; }

    public IEnumerator<T> GetEnumerator() => Provider.Execute<IEnumerable<T>>(Expression).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
