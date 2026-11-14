using System.Linq.Expressions;
using System.Reflection;
using ParquetNeo.DataModel;

namespace ParquetNeo.Linq;

/// <summary>A property of the row (or of an object in it) that is a column of the file, found in an expression.</summary>
/// <param name="Path">The names of the column or group at each step, as the file has them.</param>
/// <param name="Type">The type of the property, without the question mark of a <see cref="Nullable{T}"/>.</param>
internal sealed record ColumnReference(IReadOnlyList<string> Path, Type Type)
{
    /// <summary>Finds a column in an expression: the parameter, and properties of it, as <c>x.Address.Zip</c>; a nullable value's <c>Value</c> counts as the value.</summary>
    public static ColumnReference? TryCreate(Expression expression, ParameterExpression parameter)
    {
        var names = new List<string>();
        var current = Unwrap(expression);
        Type? type = null;
        while (current is MemberExpression { Member: PropertyInfo property } member)
        {
            // The value of a nullable is the column itself.
            if (member.Member.Name == nameof(Nullable<int>.Value) && Nullable.GetUnderlyingType(member.Expression?.Type ?? typeof(object)) is not null)
            {
                current = Unwrap(member.Expression!);
                continue;
            }

            if (property.GetCustomAttribute<ParquetIgnoreAttribute>() is not null || property.GetIndexParameters().Length > 0)
            {
                return null;
            }

            names.Insert(0, ColumnName(property));
            type ??= Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            current = member.Expression is null ? null : Unwrap(member.Expression);
        }

        return current == parameter && names.Count > 0 && type is not null ? new ColumnReference(names, type) : null;
    }

    public static string ColumnName(PropertyInfo property) => property.GetCustomAttribute<ParquetColumnAttribute>()?.Name ?? property.Name;

    /// <summary>The expression without the conversions that only widen a number or make it nullable, which do not change what is compared.</summary>
    public static Expression Unwrap(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert && IsWidening(convert.Operand.Type, convert.Type))
        {
            expression = convert.Operand;
        }

        return expression;
    }

    private static bool IsWidening(Type from, Type to)
    {
        var source = Nullable.GetUnderlyingType(from) ?? from;
        var target = Nullable.GetUnderlyingType(to) ?? to;
        if (source == target)
        {
            return true;
        }

        // The compiler compares enums as the numbers they are: the column is the enum, and the translator turns the number back.
        if (source.IsEnum)
        {
            return IsNumeric(target) && !target.IsEnum;
        }

        return IsNumeric(source) && IsNumeric(target) && !target.IsEnum && !source.IsEnum;
    }

    private static bool IsNumeric(Type type) => Type.GetTypeCode(type) is >= TypeCode.SByte and <= TypeCode.Decimal;
}
