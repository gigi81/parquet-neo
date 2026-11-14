using System.Linq.Expressions;
using System.Reflection;
using ParquetNeo.DataModel;

namespace ParquetNeo.Linq;

/// <summary>
/// What a query needs of the dataset, worked out from its expression: the condition that row groups and files are checked against,
/// the columns that are read, and the expression to run over the rows that are read. The rest of the query is what LINQ to objects does with them.
/// </summary>
internal sealed class QueryPlan
{
    private static readonly string[] OrderingMethods = ["OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending"];

    private readonly List<MethodCallExpression> _calls;
    private readonly Type? _narrow;
    private readonly int _narrowedCalls;

    private QueryPlan(DatasetSource source, Type elementType, List<MethodCallExpression> calls, ParquetFilter? filter, Type? narrow, int narrowedCalls, IReadOnlyList<string>? columns)
    {
        Source = source;
        ElementType = elementType;
        _calls = calls;
        Filter = filter;
        _narrow = narrow;
        _narrowedCalls = narrowedCalls;
        Columns = columns;
    }

    public DatasetSource Source { get; }

    /// <summary>The type of the rows of the dataset.</summary>
    public Type ElementType { get; }

    /// <summary>The type the rows are read as: the row type, or a class with only the properties the query uses.</summary>
    public Type RowType => _narrow ?? ElementType;

    public ParquetFilter? Filter { get; }

    /// <summary>The names of the columns that are read, or null when all that the row type has are.</summary>
    public IReadOnlyList<string>? Columns { get; }

    /// <summary>Whether the query is the number of rows, or whether there is any, which the footers answer.</summary>
    public string? FooterAnswer =>
        _calls.Count == 1 && _calls[0].Arguments.Count == 1 && _calls[0].Method.Name is "Count" or "LongCount" or "Any" ? _calls[0].Method.Name : null;

    /// <exception cref="NotSupportedException">The expression is not a query of a dataset.</exception>
    public static QueryPlan Analyze(DatasetSource source, Expression expression)
    {
        var calls = new List<MethodCallExpression>();
        var current = expression;
        while (current is MethodCallExpression { Method.DeclaringType: { } declaring } call && declaring == typeof(Queryable) && call.Arguments.Count > 0)
        {
            calls.Add(call);
            current = call.Arguments[0];
        }

        calls.Reverse();
        if (current is not ConstantExpression { Value: IDatasetRoot root })
        {
            throw new NotSupportedException($"The query cannot be run: it does not start from a {nameof(ParquetDataset<int>)}. Use the operators of System.Linq.Queryable on it.");
        }

        var elementType = root.ElementType;
        var filter = LeadingFilter(calls);
        var (narrow, narrowedCalls, columns) = Narrow(calls, elementType);
        return new QueryPlan(source, elementType, calls, filter, narrow, narrowedCalls, columns);
    }

    /// <summary>The query with <paramref name="root"/>, the rows that are read, in place of the dataset.</summary>
    public Expression Build(Expression root)
    {
        var current = root;
        for (int index = 0; index < _calls.Count; index++)
        {
            var call = _calls[index];
            current = index < _narrowedCalls ? Narrowed(call, current) : Expression.Call(call.Method, new[] { current }.Concat(call.Arguments.Skip(1)));
        }

        return current;
    }

    /// <summary>
    /// The condition of the <c>Where</c>s that apply to every row of the dataset: the ones that come first, and the ones that come after
    /// an ordering, which can be moved in front of it without changing what the query gives (a <c>Where</c> does not depend on the
    /// order of the rows, and ordering is stable). Hot Chocolate, for one, puts the ordering of a query before its filter.
    /// A <c>Skip</c>, a <c>Take</c>, a <c>Select</c> or any other operator stops the search: a <c>Where</c> after it is about other rows.
    /// </summary>
    private static ParquetFilter? LeadingFilter(List<MethodCallExpression> calls)
    {
        ParquetFilter? filter = null;
        foreach (var call in calls)
        {
            if (IsOrdering(call))
            {
                continue;
            }

            if (!IsWhere(call))
            {
                break;
            }

            var part = PredicateTranslator.Translate(Lambda(call.Arguments[1]));
            filter = filter is null || part is null ? filter ?? part : filter.And(part);
        }

        return filter;
    }

    private static bool IsOrdering(MethodCallExpression call) => OrderingMethods.Contains(call.Method.Name) && HasOneParameter(call);

    /// <summary>
    /// The class to read when the query goes from the rows to something else with <c>Select</c>, after only <c>Where</c>, ordering
    /// and paging, and uses only some of the properties of a row: the rows then need no more than those. Everything else reads the whole row.
    /// </summary>
    private static (Type? Narrow, int Calls, IReadOnlyList<string>? Columns) Narrow(List<MethodCallExpression> calls, Type elementType)
    {
        int select = -1;
        for (int index = 0; index < calls.Count && select < 0; index++)
        {
            var call = calls[index];
            if (call.Method.GetGenericArguments().FirstOrDefault() != elementType)
            {
                return (null, 0, null);
            }

            if (call.Method.Name == "Select" && HasOneParameter(call))
            {
                select = index;
            }
            else if (!(IsWhere(call) || IsOrdering(call) || (call.Method.Name is "Skip" or "Take" && call.Arguments.Count == 2)))
            {
                return (null, 0, null);
            }
        }

        if (select < 0)
        {
            return (null, 0, null);
        }

        var used = new List<PropertyInfo>();
        foreach (var call in calls.Take(select + 1))
        {
            foreach (var argument in call.Arguments.Skip(1).Where(a => a is UnaryExpression { NodeType: ExpressionType.Quote }))
            {
                var lambda = Lambda(argument);
                var usage = new ParameterUsage(lambda.Parameters[0]);
                usage.Visit(lambda.Body);
                if (usage.Escapes)
                {
                    return (null, 0, null);
                }

                used.AddRange(usage.Properties.Where(p => !used.Contains(p)));
            }
        }

        if (used.Count == 0 || used.Any(property => !IsColumn(property)))
        {
            return (null, 0, null);
        }

        return (NarrowTypes.Get(used), select + 1, used.Select(ColumnReference.ColumnName).ToArray());
    }

    /// <summary>Whether a property is read from a column of its own name: it can be set, and is not left out.</summary>
    private static bool IsColumn(PropertyInfo property) =>
        property is { CanRead: true, CanWrite: true }
        && property.GetMethod is { IsPublic: true, IsStatic: false }
        && property.SetMethod is { IsPublic: true }
        && property.GetCustomAttribute<ParquetIgnoreAttribute>() is null
        && property.GetIndexParameters().Length == 0;

    private Expression Narrowed(MethodCallExpression call, Expression source)
    {
        var arguments = call.Method.GetGenericArguments();
        arguments[0] = _narrow!;
        var method = call.Method.GetGenericMethodDefinition().MakeGenericMethod(arguments);
        var rest = call.Arguments.Skip(1).Select(argument => argument is UnaryExpression { NodeType: ExpressionType.Quote } quote ? Expression.Quote(Narrowed((LambdaExpression)quote.Operand)) : argument);
        return Expression.Call(method, new[] { source }.Concat(rest));
    }

    /// <summary>The lambda for the narrow class: it takes one, and its properties are the ones of the property of the row.</summary>
    private LambdaExpression Narrowed(LambdaExpression lambda)
    {
        var parameter = Expression.Parameter(_narrow!, lambda.Parameters[0].Name);
        var body = new PropertySwitch(lambda.Parameters[0], parameter).Visit(lambda.Body);
        var types = lambda.Type.GetGenericArguments();
        types[0] = _narrow!;
        return Expression.Lambda(lambda.Type.GetGenericTypeDefinition().MakeGenericType(types), body, parameter);
    }

    private static bool IsWhere(MethodCallExpression call) => call.Method.Name == "Where" && HasOneParameter(call);

    private static bool HasOneParameter(MethodCallExpression call) => call.Arguments.Count >= 2 && Lambda(call.Arguments[1]).Parameters.Count == 1;

    private static LambdaExpression Lambda(Expression argument) =>
        argument is UnaryExpression { NodeType: ExpressionType.Quote } quote ? (LambdaExpression)quote.Operand : (LambdaExpression)argument;

    /// <summary>Replaces the properties of a row with those of the narrow class.</summary>
    private sealed class PropertySwitch(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node) =>
            node.Expression == from && node.Member is PropertyInfo property ? Expression.Property(to, to.Type.GetProperty(property.Name)!) : base.VisitMember(node);
    }
}
