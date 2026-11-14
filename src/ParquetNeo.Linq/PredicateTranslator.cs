using System.Collections;
using System.Linq.Expressions;
using System.Reflection;

namespace ParquetNeo.Linq;

/// <summary>
/// Takes from the predicate of a <c>Where</c> the condition that a row group's statistics can be checked against. What is taken is
/// always implied by the predicate (every row that satisfies it satisfies the condition), so a row group the condition rules out has no
/// row the predicate holds for; the predicate itself is still applied to the rows that are read, so what the condition leaves
/// out costs time, never correctness.
/// </summary>
internal static class PredicateTranslator
{
    /// <summary>The condition, or null when nothing of the predicate can be checked against statistics.</summary>
    public static ParquetFilter? Translate(LambdaExpression predicate)
    {
        if (predicate.Parameters.Count != 1)
        {
            return null;
        }

        return Translate(predicate.Body, predicate.Parameters[0], out _);
    }

    /// <param name="exact">Whether the condition holds for exactly the rows the expression is true for, which negating it needs.</param>
    private static ParquetFilter? Translate(Expression expression, ParameterExpression parameter, out bool exact)
    {
        exact = false;
        switch (expression.NodeType)
        {
            case ExpressionType.AndAlso:
                return Both((BinaryExpression)expression, parameter, isAnd: true, out exact);
            case ExpressionType.OrElse:
                return Both((BinaryExpression)expression, parameter, isAnd: false, out exact);
            case ExpressionType.Not:
                return Negate(((UnaryExpression)expression).Operand, parameter, out exact);
            case ExpressionType.Equal:
            case ExpressionType.NotEqual:
            case ExpressionType.LessThan:
            case ExpressionType.LessThanOrEqual:
            case ExpressionType.GreaterThan:
            case ExpressionType.GreaterThanOrEqual:
                return Comparison((BinaryExpression)expression, parameter, out exact);
            case ExpressionType.MemberAccess:
                return Flag((MemberExpression)expression, parameter, out exact);
            case ExpressionType.Call:
                return Contains((MethodCallExpression)expression, parameter, out exact);
            default:
                return null;
        }
    }

    private static ParquetFilter? Both(BinaryExpression expression, ParameterExpression parameter, bool isAnd, out bool exact)
    {
        var left = Translate(expression.Left, parameter, out bool leftExact);
        var right = Translate(expression.Right, parameter, out bool rightExact);
        exact = leftExact && rightExact;
        if (isAnd)
        {
            // A part that cannot be checked is left out: what remains is implied all the same.
            if (left is null || right is null)
            {
                return left ?? right;
            }

            return left.And(right);
        }

        // For "or", a part that cannot be checked could hold for any row, so the whole cannot be checked.
        return left is null || right is null ? null : left.Or(right);
    }

    private static ParquetFilter? Negate(Expression operand, ParameterExpression parameter, out bool exact)
    {
        var inner = Translate(operand, parameter, out bool innerExact);
        exact = innerExact;

        // Not of a condition that is only implied by the predicate would no longer be implied.
        return inner is not null && innerExact ? inner.Not() : null;
    }

    private static ParquetFilter? Comparison(BinaryExpression expression, ParameterExpression parameter, out bool exact)
    {
        exact = false;
        var type = expression.NodeType;
        var column = ColumnReference.TryCreate(expression.Left, parameter);
        var other = expression.Right;
        if (column is null)
        {
            column = ColumnReference.TryCreate(expression.Right, parameter);
            other = expression.Left;
            type = Flip(type);
        }

        if (column is null || !TryEvaluate(other, parameter, out object? value))
        {
            return null;
        }

        var target = ParquetFilter.Column(column.Path.ToArray());
        if (value is null)
        {
            // Compared with null: only equality says anything, and a comparison for order with a null is false for every row.
            exact = type is ExpressionType.Equal or ExpressionType.NotEqual;
            return type switch
            {
                ExpressionType.Equal => target.IsNull(),
                ExpressionType.NotEqual => target.IsNotNull(),
                _ => null,
            };
        }

        value = AsColumnType(column.Type, value);
        if (!Suits(column.Type, value))
        {
            return null;
        }

        exact = true;
        return type switch
        {
            ExpressionType.Equal => target.Equal(value),
            ExpressionType.NotEqual => target.NotEqual(value),
            ExpressionType.LessThan => target.LessThan(value),
            ExpressionType.LessThanOrEqual => target.LessThanOrEqual(value),
            ExpressionType.GreaterThan => target.GreaterThan(value),
            _ => target.GreaterThanOrEqual(value),
        };
    }

    /// <summary>A boolean property used as a condition: <c>x.Active</c>, or <c>x.Value.HasValue</c> for a nullable.</summary>
    private static ParquetFilter? Flag(MemberExpression expression, ParameterExpression parameter, out bool exact)
    {
        exact = false;
        if (expression.Member.Name == nameof(Nullable<int>.HasValue) && expression.Expression is { } owner && Nullable.GetUnderlyingType(owner.Type) is not null
            && ColumnReference.TryCreate(owner, parameter) is { } nullable)
        {
            exact = true;
            return ParquetFilter.Column(nullable.Path.ToArray()).IsNotNull();
        }

        if (expression.Type == typeof(bool) && ColumnReference.TryCreate(expression, parameter) is { } flag)
        {
            exact = true;
            return ParquetFilter.Column(flag.Path.ToArray()).Equal(true);
        }

        return null;
    }

    /// <summary><c>values.Contains(x.Id)</c>, for a collection that does not depend on the row.</summary>
    private static ParquetFilter? Contains(MethodCallExpression call, ParameterExpression parameter, out bool exact)
    {
        exact = false;
        Expression? collection;
        Expression? item;
        if (call.Method.Name != nameof(Enumerable.Contains))
        {
            return null;
        }

        if (call.Method.DeclaringType == typeof(Enumerable) && call.Arguments.Count == 2)
        {
            (collection, item) = (call.Arguments[0], call.Arguments[1]);
        }
        else if (call.Method.DeclaringType == typeof(MemoryExtensions) && call.Arguments.Count == 2)
        {
            // The compiler calls the span version for an array: the array is what the span is made from.
            collection = call.Arguments[0] is MethodCallExpression { Method.Name: "op_Implicit", Arguments.Count: 1 } conversion ? conversion.Arguments[0] : call.Arguments[0];
            item = call.Arguments[1];
        }
        else if (call.Object is not null && call.Arguments.Count == 1 && typeof(IEnumerable).IsAssignableFrom(call.Object.Type))
        {
            (collection, item) = (call.Object, call.Arguments[0]);
        }
        else
        {
            return null;
        }

        var column = ColumnReference.TryCreate(item, parameter);
        if (column is null || !TryEvaluate(collection, parameter, out object? values) || values is not IEnumerable list || values is string)
        {
            return null;
        }

        var candidates = list.Cast<object?>().Select(value => value is null ? null : AsColumnType(column.Type, value)).ToArray();
        if (candidates.Length == 0 || candidates.Any(value => value is not null && !Suits(column.Type, value)))
        {
            return null;
        }

        exact = true;
        return ParquetFilter.Column(column.Path.ToArray()).In(candidates);
    }

    private static ExpressionType Flip(ExpressionType type) => type switch
    {
        ExpressionType.LessThan => ExpressionType.GreaterThan,
        ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
        ExpressionType.GreaterThan => ExpressionType.LessThan,
        ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
        _ => type,
    };

    /// <summary>The value of an expression that does not depend on the row: a constant, or a captured variable.</summary>
    private static bool TryEvaluate(Expression expression, ParameterExpression parameter, out object? value)
    {
        value = null;
        if (new ParameterFinder(parameter).Uses(expression))
        {
            return false;
        }

        try
        {
            value = Expression.Lambda(Expression.Convert(ColumnReference.Unwrap(expression), typeof(object))).Compile().DynamicInvoke();
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or TargetInvocationException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>A number compared with an enum column, which is what the compiler makes of a comparison with an enum, as the enum.</summary>
    private static object AsColumnType(Type column, object value) =>
        column.IsEnum && !value.GetType().IsEnum && Type.GetTypeCode(value.GetType()) is >= TypeCode.SByte and <= TypeCode.UInt64 ? Enum.ToObject(column, value) : value;

    /// <summary>Whether a value is of a type the statistics of a column of the type can be compared with, so that the filter cannot be refused.</summary>
    private static bool Suits(Type column, object value)
    {
        var valueType = value.GetType();
        if (column.IsEnum)
        {
            return valueType == column;
        }

        return Type.GetTypeCode(column) switch
        {
            >= TypeCode.SByte and <= TypeCode.UInt64 => Type.GetTypeCode(valueType) is >= TypeCode.SByte and <= TypeCode.UInt64 && !valueType.IsEnum,
            TypeCode.Single or TypeCode.Double => Type.GetTypeCode(valueType) is >= TypeCode.SByte and <= TypeCode.Double && !valueType.IsEnum,
            TypeCode.Decimal => Type.GetTypeCode(valueType) is >= TypeCode.SByte and <= TypeCode.UInt64 or TypeCode.Decimal && !valueType.IsEnum,
            _ => column == typeof(Half) ? valueType == typeof(Half) || Type.GetTypeCode(valueType) is >= TypeCode.SByte and <= TypeCode.Double : valueType == column,
        };
    }

    private sealed class ParameterFinder(ParameterExpression parameter) : ExpressionVisitor
    {
        private bool _found;

        public bool Uses(Expression expression)
        {
            Visit(expression);
            return _found;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            _found |= node == parameter;
            return node;
        }
    }
}
