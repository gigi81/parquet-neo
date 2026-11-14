using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;

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
            return (TResult)AnswerFromFooters(answer);
        }

        var rows = Read(plan);
        var queryable = AsQueryable(plan.RowType, rows);
        var query = plan.Build(Expression.Constant(queryable, typeof(IQueryable<>).MakeGenericType(plan.RowType)));
        return queryable.Provider.Execute<TResult>(query);
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

    private object AnswerFromFooters(string answer)
    {
        long rows = source.CountRows();
        return answer switch
        {
            "Count" => checked((int)rows),
            "LongCount" => rows,
            _ => rows > 0,
        };
    }

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
