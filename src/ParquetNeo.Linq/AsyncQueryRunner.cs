using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace ParquetNeo.Linq;

/// <summary>
/// Runs a query without a thread waiting for the files. The rows are read with asynchronous calls and go through the operators that
/// can work on rows as they arrive (<c>Where</c>, <c>Select</c>, <c>Skip</c>, <c>Take</c>, and <c>First</c>, <c>FirstOrDefault</c>,
/// <c>Any</c>, <c>Count</c> and <c>LongCount</c> to end with), as asynchronous LINQ. The first operator that needs every row
/// (<c>OrderBy</c>, <c>GroupBy</c>, <c>Join</c>, <c>Distinct</c>, an aggregate, ...) is given the rows after they have been awaited into a
/// list, which is also what it holds in LINQ to objects, and that operator and the ones after it run as LINQ to objects does, over memory.
/// So an operator means what it always does, and no query holds more rows than the synchronous one.
/// </summary>
internal static class AsyncQueryRunner
{
    private const BindingFlags Helpers = BindingFlags.Public | BindingFlags.Static;

    private static readonly string[] Terminals = ["First", "FirstOrDefault", "Any", "Count", "LongCount"];

    private static readonly MethodInfo ReadAsyncMethod = typeof(DatasetSource).GetMethod(nameof(DatasetSource.ReadAsync))!;
    private static readonly MethodInfo AsQueryableMethod = typeof(Queryable).GetMethods()
        .Single(method => method.Name == nameof(Queryable.AsQueryable) && method.IsGenericMethod && method.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>));

    /// <summary>
    /// Starts the query. The result is an <see cref="IAsyncEnumerable{T}"/> (nothing is read until it is enumerated) or, when the query has
    /// to hold every row for an operator, an <see cref="IEnumerable{T}"/> over rows in memory, for a query that gives rows; and the value
    /// of the query, for one that gives one.
    /// </summary>
    public static async ValueTask<object?> RunAsync(DatasetSource source, QueryPlan plan, CancellationToken cancellationToken)
    {
        var elementType = plan.RowType;
        object current = Invoke(ReadAsyncMethod.MakeGenericMethod(elementType), source, plan.Filter, cancellationToken)!;
        for (int index = 0; index < plan.CallCount; index++)
        {
            var call = plan.CallOn(index, Expression.Parameter(typeof(IQueryable<>).MakeGenericType(elementType)));
            if (!IsTerminal(call))
            {
                if (!TryStream(call, ref current, ref elementType))
                {
                    return await FinishInMemoryAsync(plan, index, current, elementType, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            if (!IsStreamingTerminal(call))
            {
                return await FinishInMemoryAsync(plan, index, current, elementType, cancellationToken).ConfigureAwait(false);
            }

            var predicate = call.Arguments.Count == 2 ? Lambda(call.Arguments[1]) : null;
            return await ((Task<object?>)Invoke(Helper(nameof(Terminal), elementType), null, current, call.Method.Name, predicate, cancellationToken)!).ConfigureAwait(false);
        }

        return current;
    }

    /// <summary>Turns an operator that works on rows as they come into its asynchronous form; false when it is not one of those.</summary>
    private static bool TryStream(MethodCallExpression call, ref object current, ref Type elementType)
    {
        var types = call.Method.GetGenericArguments();
        switch (call.Method.Name)
        {
            case "Where" when HasOneParameter(call):
                current = Invoke(Helper(nameof(Where), elementType), null, current, Lambda(call.Arguments[1]))!;
                return true;
            case "Select" when HasOneParameter(call):
                current = Invoke(Helper(nameof(Select), types[0], types[1]), null, current, Lambda(call.Arguments[1]))!;
                elementType = types[1];
                return true;
            case "Skip" when call.Arguments.Count == 2 && call.Arguments[1].Type == typeof(int):
                current = Invoke(Helper(nameof(Skip), elementType), null, current, Evaluate(call.Arguments[1]))!;
                return true;
            case "Take" when call.Arguments.Count == 2 && call.Arguments[1].Type == typeof(int):
                current = Invoke(Helper(nameof(Take), elementType), null, current, Evaluate(call.Arguments[1]))!;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Awaits the rows so far into a list and runs the operator at <paramref name="index"/>, and the ones after it, over them as LINQ to objects does.</summary>
    private static async ValueTask<object?> FinishInMemoryAsync(QueryPlan plan, int index, object rows, Type elementType, CancellationToken cancellationToken)
    {
        var list = await ((Task<object>)Invoke(Helper(nameof(ToList), elementType), null, rows, cancellationToken)!).ConfigureAwait(false);
        var queryable = (IQueryable)Invoke(AsQueryableMethod.MakeGenericMethod(elementType), null, list)!;
        var query = plan.Build(Expression.Constant(queryable, typeof(IQueryable<>).MakeGenericType(elementType)), index);
        if (typeof(IQueryable).IsAssignableFrom(query.Type))
        {
            return queryable.Provider.CreateQuery(query);
        }

        return await Task.Run(() => queryable.Provider.Execute(query), cancellationToken).ConfigureAwait(false);
    }

    private static bool IsTerminal(MethodCallExpression call) => !typeof(IQueryable).IsAssignableFrom(call.Type);

    private static bool IsStreamingTerminal(MethodCallExpression call) =>
        Terminals.Contains(call.Method.Name) && (call.Arguments.Count == 1 || (call.Arguments.Count == 2 && HasOneParameter(call)));

    private static bool HasOneParameter(MethodCallExpression call) => call.Arguments.Count == 2 && Lambda(call.Arguments[1]).Parameters.Count == 1;

    private static LambdaExpression Lambda(Expression argument) =>
        argument is UnaryExpression { NodeType: ExpressionType.Quote } quote ? (LambdaExpression)quote.Operand : (LambdaExpression)argument;

    private static object Evaluate(Expression argument) => Expression.Lambda(argument).Compile().DynamicInvoke()!;

    private static MethodInfo Helper(string name, params Type[] types) => typeof(AsyncQueryRunner).GetMethod(name, Helpers)!.MakeGenericMethod(types);

    /// <summary>Calls a method by reflection, giving the exception it throws as it is.</summary>
    private static object? Invoke(MethodInfo method, object? target, params object?[] arguments)
    {
        try
        {
            return method.Invoke(target, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    public static IAsyncEnumerable<T> Where<T>(IAsyncEnumerable<T> rows, LambdaExpression predicate) => rows.Where((Func<T, bool>)predicate.Compile());

    public static IAsyncEnumerable<TResult> Select<T, TResult>(IAsyncEnumerable<T> rows, LambdaExpression selector) => rows.Select((Func<T, TResult>)selector.Compile());

    public static IAsyncEnumerable<T> Skip<T>(IAsyncEnumerable<T> rows, int count) => rows.Skip(count);

    public static IAsyncEnumerable<T> Take<T>(IAsyncEnumerable<T> rows, int count) => rows.Take(count);

    public static async Task<object> ToList<T>(IAsyncEnumerable<T> rows, CancellationToken cancellationToken) =>
        (IEnumerable)await rows.ToListAsync(cancellationToken).ConfigureAwait(false);

    public static async Task<object?> Terminal<T>(IAsyncEnumerable<T> rows, string name, LambdaExpression? predicate, CancellationToken cancellationToken)
    {
        var test = (Func<T, bool>?)predicate?.Compile();
        return name switch
        {
            "First" => test is null ? await rows.FirstAsync(cancellationToken).ConfigureAwait(false) : await rows.FirstAsync(test, cancellationToken).ConfigureAwait(false),
            "FirstOrDefault" => test is null ? await rows.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) : await rows.FirstOrDefaultAsync(test, cancellationToken).ConfigureAwait(false),
            "Any" => test is null ? await rows.AnyAsync(cancellationToken).ConfigureAwait(false) : await rows.AnyAsync(test, cancellationToken).ConfigureAwait(false),
            "Count" => test is null ? await rows.CountAsync(cancellationToken).ConfigureAwait(false) : await rows.CountAsync(test, cancellationToken).ConfigureAwait(false),
            _ => test is null ? await rows.LongCountAsync(cancellationToken).ConfigureAwait(false) : await rows.LongCountAsync(test, cancellationToken).ConfigureAwait(false),
        };
    }
}
