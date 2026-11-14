using System.Collections;
using System.Linq.Expressions;

namespace ParquetNeo.Linq;

/// <summary>A query of a dataset that has operators applied: what <c>Where</c>, <c>Select</c> and the others return.</summary>
internal sealed class ParquetQuery<T>(IQueryProvider provider, Expression expression) : IOrderedQueryable<T>
{
    public Type ElementType => typeof(T);

    public Expression Expression { get; } = expression;

    public IQueryProvider Provider { get; } = provider;

    public IEnumerator<T> GetEnumerator() => Provider.Execute<IEnumerable<T>>(Expression).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
