using System.Collections;
using System.Linq.Expressions;

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

    public Type ElementType => typeof(T);

    public Expression Expression { get; }

    public IQueryProvider Provider { get; }

    internal DatasetSource Source { get; }

    public IEnumerator<T> GetEnumerator() => Provider.Execute<IEnumerable<T>>(Expression).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
