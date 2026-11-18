using System.Linq.Expressions;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using ParquetNeo.Diagnostics;

namespace ParquetNeo.Linq;

/// <summary>
/// Runs the queries of a dataset. The condition of the leading <c>Where</c>s is checked against the statistics of files and row
/// groups, and the columns the query uses are the only ones read (when it ends in a <c>Select</c>); then the rows that are read
/// go through the operators of the query as LINQ to objects runs them, so every operator means what it always does.
/// </summary>
internal sealed class ParquetQueryProvider(DatasetSource source) : IQueryProvider
{
    private static readonly MethodInfo AsQueryableMethod = typeof(Queryable).GetMethods()
        .Single(method => method.Name == nameof(Queryable.AsQueryable) && method.IsGenericMethod && method.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>));

    private static readonly MethodInfo ReadMethod = typeof(DatasetSource).GetMethod(nameof(DatasetSource.Read))!;

    public IQueryable CreateQuery(Expression expression)
    {
        var element = ElementOf(expression.Type);
        return (IQueryable)Activator.CreateInstance(typeof(ParquetQuery<>).MakeGenericType(element), this, expression)!;
    }

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => new ParquetQuery<TElement>(this, expression);

    public object? Execute(Expression expression) =>
        typeof(ParquetQueryProvider).GetMethod(nameof(Execute), 1, [typeof(Expression)])!.MakeGenericMethod(expression.Type).Invoke(this, [expression]);

    public TResult Execute<TResult>(Expression expression)
    {
        var plan = QueryPlan.Analyze(source, expression);
        if (plan.FooterAnswer is { } answer)
        {
            long started = Stopwatch.GetTimestamp();
            var result = (TResult)AnswerFromFooters(answer);
            source.Options.Metrics?.RecordQuery(Stopwatch.GetElapsedTime(started).TotalSeconds, 0, filtered: false);
            return result;
        }

        var rows = Read(plan);
        var queryable = AsQueryable(plan.RowType, rows);
        var query = plan.Build(Expression.Constant(queryable, typeof(IQueryable<>).MakeGenericType(plan.RowType)));
        return queryable.Provider.Execute<TResult>(query);
    }

    /// <summary>The rows of a query, read without a thread waiting for the files; see <see cref="AsyncQueryRunner"/>.</summary>
    public async IAsyncEnumerable<TElement> ExecuteSequenceAsync<TElement>(Expression expression, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var plan = QueryPlan.Analyze(source, expression);
        var result = await AsyncQueryRunner.RunAsync(source, plan, cancellationToken).ConfigureAwait(false);
        if (result is IAsyncEnumerable<TElement> stream)
        {
            await foreach (var row in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return row;
            }

            yield break;
        }

        // The rows are in memory, and the operator that held them has still to run: its first row is where it does the work, so that is not done on the caller's thread.
        using var enumerator = ((IEnumerable<TElement>)result!).GetEnumerator();
        if (!await Task.Run(enumerator.MoveNext, cancellationToken).ConfigureAwait(false))
        {
            yield break;
        }

        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return enumerator.Current;
        }
        while (enumerator.MoveNext());
    }

    /// <summary>The value of a query that gives one (<c>First</c>, <c>Count</c>, ...), read without a thread waiting for the files.</summary>
    public async Task<TResult> ExecuteScalarAsync<TResult>(Expression expression, CancellationToken cancellationToken)
    {
        var plan = QueryPlan.Analyze(source, expression);
        if (plan.FooterAnswer is { } answer)
        {
            long started = Stopwatch.GetTimestamp();
            long rows = await source.CountRowsAsync(cancellationToken).ConfigureAwait(false);
            source.Options.Metrics?.RecordQuery(Stopwatch.GetElapsedTime(started).TotalSeconds, 0, filtered: false);
            return (TResult)Answer(answer, rows);
        }

        return (TResult)(await AsyncQueryRunner.RunAsync(source, plan, cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>What a query reads of the dataset, from the footers of its files, read without blocking.</summary>
    public async Task<ParquetQueryPlan> ExplainAsync(Expression expression, CancellationToken cancellationToken)
    {
        var plan = QueryPlan.Analyze(source, expression);
        if (plan.FooterAnswer is not null)
        {
            int files = (await source.FilesAsync(cancellationToken).ConfigureAwait(false)).Count;
            return new ParquetQueryPlan(files, 0, 0, 0, null, null, AnsweredFromMetadata: true);
        }

        var (total, skipped, groups, skippedGroups) = await source.InspectAsync(plan.Filter, cancellationToken).ConfigureAwait(false);
        return new ParquetQueryPlan(total, skipped, groups, skippedGroups, plan.Filter?.ToString(), plan.Columns, AnsweredFromMetadata: false);
    }

    /// <summary>What a query reads of the dataset, from the footers of its files.</summary>
    public ParquetQueryPlan Explain(Expression expression)
    {
        var plan = QueryPlan.Analyze(source, expression);
        if (plan.FooterAnswer is not null)
        {
            var files = source.Files().Count;
            return new ParquetQueryPlan(files, 0, 0, 0, null, null, AnsweredFromMetadata: true);
        }

        var (total, skipped, groups, skippedGroups) = source.Inspect(plan.Filter);
        return new ParquetQueryPlan(total, skipped, groups, skippedGroups, plan.Filter?.ToString(), plan.Columns, AnsweredFromMetadata: false);
    }

    private object AnswerFromFooters(string answer) => Answer(answer, source.CountRows());

    private static object Answer(string answer, long rows) => answer switch
    {
        "Count" => checked((int)rows),
        "LongCount" => rows,
        _ => rows > 0,
    };

    private System.Collections.IEnumerable Read(QueryPlan plan)
    {
        try
        {
            return (System.Collections.IEnumerable)ReadMethod.MakeGenericMethod(plan.RowType).Invoke(source, [plan.Filter])!;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static IQueryable AsQueryable(Type rowType, System.Collections.IEnumerable rows) =>
        (IQueryable)AsQueryableMethod.MakeGenericMethod(rowType).Invoke(null, [rows])!;

    private static Type ElementOf(Type type)
    {
        var queryable = type.GetInterfaces().Append(type).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IQueryable<>));
        return queryable?.GetGenericArguments()[0] ?? throw new ArgumentException($"{type} is not a query.", nameof(type));
    }
}
