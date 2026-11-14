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
    /// The rows of the query, without blocking the caller: the query is run on another thread, which waits when the caller is slow to
    /// take the rows, so that no more than a few are held back.
    /// </summary>
    public static IAsyncEnumerable<T> AsAsyncEnumerable<T>(this IQueryable<T> query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Iterate(query, cancellationToken);
    }

    private static async IAsyncEnumerable<T> Iterate<T>(IQueryable<T> query, [EnumeratorCancellation] CancellationToken cancellationToken)
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

    /// <summary>The rows of the query in a list, without blocking the caller.</summary>
    public static async Task<List<T>> ToListAsync<T>(this IQueryable<T> query, CancellationToken cancellationToken = default)
    {
        var rows = new List<T>();
        await foreach (var row in query.AsAsyncEnumerable(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>The rows of the query in an array, without blocking the caller.</summary>
    public static async Task<T[]> ToArrayAsync<T>(this IQueryable<T> query, CancellationToken cancellationToken = default) =>
        (await query.ToListAsync(cancellationToken).ConfigureAwait(false)).ToArray();

    /// <summary>The first row of the query, without blocking the caller.</summary>
    /// <exception cref="InvalidOperationException">The query has no rows.</exception>
    public static Task<T> FirstAsync<T>(this IQueryable<T> query, CancellationToken cancellationToken = default) => Run(() => query.First(), cancellationToken);

    /// <summary>The first row of the query that satisfies the predicate, without blocking the caller.</summary>
    /// <exception cref="InvalidOperationException">No row does.</exception>
    public static Task<T> FirstAsync<T>(this IQueryable<T> query, Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) => Run(() => query.First(predicate), cancellationToken);

    /// <summary>The first row of the query, or the default of the type when there is none, without blocking the caller.</summary>
    public static Task<T?> FirstOrDefaultAsync<T>(this IQueryable<T> query, CancellationToken cancellationToken = default) => Run(() => query.FirstOrDefault(), cancellationToken);

    /// <summary>The first row of the query that satisfies the predicate, or the default of the type when none does, without blocking the caller.</summary>
    public static Task<T?> FirstOrDefaultAsync<T>(this IQueryable<T> query, Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) => Run(() => query.FirstOrDefault(predicate), cancellationToken);

    /// <summary>The number of rows of the query, without blocking the caller.</summary>
    public static Task<int> CountAsync<T>(this IQueryable<T> query, CancellationToken cancellationToken = default) => Run(() => query.Count(), cancellationToken);

    /// <summary>The number of rows of the query that satisfy the predicate, without blocking the caller.</summary>
    public static Task<int> CountAsync<T>(this IQueryable<T> query, Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) => Run(() => query.Count(predicate), cancellationToken);

    /// <summary>Whether the query has any row, without blocking the caller.</summary>
    public static Task<bool> AnyAsync<T>(this IQueryable<T> query, CancellationToken cancellationToken = default) => Run(() => query.Any(), cancellationToken);

    /// <summary>Whether any row of the query satisfies the predicate, without blocking the caller.</summary>
    public static Task<bool> AnyAsync<T>(this IQueryable<T> query, Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) => Run(() => query.Any(predicate), cancellationToken);

    private static Task<TResult> Run<TResult>(Func<TResult> run, CancellationToken cancellationToken) => Task.Run(run, cancellationToken);
}
