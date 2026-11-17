using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace ParquetNeo.Linq;

/// <summary>Operations on the queries of a <see cref="ParquetDataset{T}"/> that LINQ does not have.</summary>
public static class ParquetQueryableExtensions
{
    /// <summary>
    /// What the query reads: how many files and row groups the statistics of their columns rule out, and which columns are read.
    /// Only the footers are read to tell.
    /// </summary>
    /// <exception cref="ArgumentException">The query is not one of a <see cref="ParquetDataset{T}"/>.</exception>
    public static ParquetQueryPlan Explain<T>(this IQueryable<T> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.Provider is ParquetQueryProvider provider
            ? provider.Explain(query.Expression)
            : throw new ArgumentException("The query is not one of a ParquetDataset.", nameof(query));
    }

    /// <summary>
    /// What the query reads, like <see cref="Explain{T}(IQueryable{T})"/>, with the footers of the files read without blocking.
    /// </summary>
    /// <exception cref="ArgumentException">The query is not one of a <see cref="ParquetDataset{T}"/>.</exception>
    public static Task<ParquetQueryPlan> ExplainAsync<T>(this IQueryable<T> query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.Provider is ParquetQueryProvider provider
            ? provider.ExplainAsync(query.Expression, cancellationToken)
            : throw new ArgumentException("The query is not one of a ParquetDataset.", nameof(query));
    }

    /// <summary>
    /// The rows of the query, read without a thread waiting for the files: they are read with asynchronous calls, by as many readers
    /// as the dataset allows, and go through <c>Where</c>, <c>Select</c>, <c>Skip</c> and <c>Take</c> as they arrive. An operator that
    /// needs every row (<c>OrderBy</c>, <c>GroupBy</c>, <c>Join</c>, ...) is given them in memory, as it would hold them anyway. No more
    /// rows are read ahead than a bounded few, and stopping the enumeration stops the reading.
    /// </summary>
    /// <remarks>A query that is not one of a <see cref="ParquetDataset{T}"/> is run on another thread.</remarks>
    public static IAsyncEnumerable<T> AsAsyncEnumerable<T>(this IQueryable<T> query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.Provider is ParquetQueryProvider provider
            ? provider.ExecuteSequenceAsync<T>(query.Expression, cancellationToken)
            : IterateOnAThread(query, cancellationToken);
    }

    private static async IAsyncEnumerable<T> IterateOnAThread<T>(IQueryable<T> query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(1024) { SingleReader = true, SingleWriter = true });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = Task.Run(
            async () =>
            {
                try
                {
                    foreach (var row in query)
                    {
                        await channel.Writer.WriteAsync(row, stop.Token).ConfigureAwait(false);
                    }

                    channel.Writer.Complete();
                }
                catch (Exception exception)
                {
                    channel.Writer.Complete(exception);
                }
            },
            CancellationToken.None);

        try
        {
            await foreach (var row in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return row;
            }
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await producer.ConfigureAwait(false);
        }
    }

    /// <summary>The rows of the query in a list, read without a thread waiting for the files.</summary>
    public static async Task<List<T>> ToListAsync<T>(this IQueryable<T> query, CancellationToken cancellationToken = default)
    {
        var rows = new List<T>();
        await foreach (var row in query.AsAsyncEnumerable(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>The rows of the query in an array, read without a thread waiting for the files.</summary>
    public static async Task<T[]> ToArrayAsync<T>(this IQueryable<T> query, CancellationToken cancellationToken = default) =>
        (await query.ToListAsync(cancellationToken).ConfigureAwait(false)).ToArray();

    /// <summary>The first row of the query, read without a thread waiting for the files; the reading stops there.</summary>
    /// <exception cref="InvalidOperationException">The query has no rows.</exception>
    public static Task<T> FirstAsync<T>(this IQueryable<T> query, CancellationToken cancellationToken = default) =>
        Terminal(query, "First", predicate: null, () => query.First(), cancellationToken);

    /// <summary>The first row of the query that satisfies the predicate, read without a thread waiting for the files; the reading stops there.</summary>
    /// <exception cref="InvalidOperationException">No row does.</exception>
    public static Task<T> FirstAsync<T>(this IQueryable<T> query, Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) =>
        Terminal(query, "First", predicate, () => query.First(predicate), cancellationToken);

    /// <summary>The first row of the query, or the default of the type when there is none, read without a thread waiting for the files.</summary>
    public static Task<T?> FirstOrDefaultAsync<T>(this IQueryable<T> query, CancellationToken cancellationToken = default) =>
        Terminal<T, T?>(query, "FirstOrDefault", predicate: null, () => query.FirstOrDefault(), cancellationToken);

    /// <summary>The first row of the query that satisfies the predicate, or the default of the type when none does, read without a thread waiting for the files.</summary>
    public static Task<T?> FirstOrDefaultAsync<T>(this IQueryable<T> query, Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) =>
        Terminal<T, T?>(query, "FirstOrDefault", predicate, () => query.FirstOrDefault(predicate), cancellationToken);

    /// <summary>The number of rows of the query, read without a thread waiting for the files (a dataset with no operators is counted from the footers).</summary>
    public static Task<int> CountAsync<T>(this IQueryable<T> query, CancellationToken cancellationToken = default) =>
        Terminal(query, "Count", predicate: null, () => query.Count(), cancellationToken);

    /// <summary>The number of rows of the query that satisfy the predicate, read without a thread waiting for the files.</summary>
    public static Task<int> CountAsync<T>(this IQueryable<T> query, Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) =>
        Terminal(query, "Count", predicate, () => query.Count(predicate), cancellationToken);

    /// <summary>Whether the query has any row, read without a thread waiting for the files; the reading stops at the first.</summary>
    public static Task<bool> AnyAsync<T>(this IQueryable<T> query, CancellationToken cancellationToken = default) =>
        Terminal(query, "Any", predicate: null, () => query.Any(), cancellationToken);

    /// <summary>Whether any row of the query satisfies the predicate, read without a thread waiting for the files; the reading stops at the first.</summary>
    public static Task<bool> AnyAsync<T>(this IQueryable<T> query, Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) =>
        Terminal(query, "Any", predicate, () => query.Any(predicate), cancellationToken);

    /// <summary>
    /// A query that gives one value: the operator of <see cref="Queryable"/> called <paramref name="name"/> applied to the query, with the
    /// predicate if there is one. A query that is not one of a dataset runs <paramref name="fallback"/> on another thread.
    /// </summary>
    private static Task<TResult> Terminal<T, TResult>(IQueryable<T> query, string name, Expression<Func<T, bool>>? predicate, Func<TResult> fallback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Provider is not ParquetQueryProvider provider)
        {
            return Task.Run(fallback, cancellationToken);
        }

        var call = predicate is null
            ? Expression.Call(typeof(Queryable), name, [typeof(T)], query.Expression)
            : Expression.Call(typeof(Queryable), name, [typeof(T)], query.Expression, Expression.Quote(predicate));
        return provider.ExecuteScalarAsync<TResult>(call, cancellationToken);
    }
}
