using System.Linq.Expressions;
using System.Reflection;

namespace ParquetNeo.Linq;

/// <summary>Finds what an expression takes of the row it is given: the properties of it that are read, and whether the row is used in any other way.</summary>
internal sealed class ParameterUsage(ParameterExpression parameter) : ExpressionVisitor
{
    private readonly List<PropertyInfo> _properties = [];

    /// <summary>Whether the row is used as a whole (passed on, compared, called on): then nothing can be said of the properties it needs.</summary>
    public bool Escapes { get; private set; }

    public IReadOnlyList<PropertyInfo> Properties => _properties;

    protected override Expression VisitMember(MemberExpression node)
    {
        if (node.Expression == parameter && node.Member is PropertyInfo property)
        {
            if (!_properties.Contains(property))
            {
                _properties.Add(property);
            }

            return node;
        }

        return base.VisitMember(node);
    }

    protected override Expression VisitParameter(ParameterExpression node)
    {
        if (node == parameter)
        {
            Escapes = true;
        }

        return node;
    }
}
