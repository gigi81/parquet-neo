using ParquetNeo.Filtering;

namespace ParquetNeo;

/// <summary>
/// A column a condition is put on; see <see cref="ParquetFilter.Column(string[])"/>. When it comes from <see cref="ParquetFilter.And(string[])"/> or
/// <see cref="ParquetFilter.Or(string[])"/>, the condition that is put on it is joined to the filter it continues, which is what the methods return.
/// </summary>
public sealed class FilterColumn
{
    private readonly string[] _path;
    private readonly ParquetFilter? _left;
    private readonly bool _isAnd;

    internal FilterColumn(string[] path, ParquetFilter? left = null, bool isAnd = true)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0 || path.Any(string.IsNullOrEmpty))
        {
            throw new ArgumentException("A column is named by a path of names that are not empty.", nameof(path));
        }

        _path = (string[])path.Clone();
        _left = left;
        _isAnd = isAnd;
    }

    /// <summary>The column holds the value; for a null, the column is null.</summary>
    /// <param name="value">A value of a type that suits the column: a number for a number column, a <see cref="string"/> for text, a <see cref="DateTime"/> or <see cref="DateTimeOffset"/> for a timestamp, and so on.</param>
    /// <exception cref="ArgumentException">At evaluation: the value is of a type that does not suit the column.</exception>
    public ParquetFilter Equal(object? value) => Join(EqualTo(value));

    /// <summary>The column holds another value, or is null; for a null, the column is not null.</summary>
    public ParquetFilter NotEqual(object? value) => Join(EqualTo(value).Not());

    /// <summary>The column holds a value less than <paramref name="value"/>.</summary>
    public ParquetFilter LessThan(object value) => Join(Compare(ComparisonKind.LessThan, value));

    /// <summary>The column holds a value less than or equal to <paramref name="value"/>.</summary>
    public ParquetFilter LessThanOrEqual(object value) => Join(Compare(ComparisonKind.LessThanOrEqual, value));

    /// <summary>The column holds a value greater than <paramref name="value"/>.</summary>
    public ParquetFilter GreaterThan(object value) => Join(Compare(ComparisonKind.GreaterThan, value));

    /// <summary>The column holds a value greater than or equal to <paramref name="value"/>.</summary>
    public ParquetFilter GreaterThanOrEqual(object value) => Join(Compare(ComparisonKind.GreaterThanOrEqual, value));

    /// <summary>The column holds a value from <paramref name="low"/> to <paramref name="high"/>, both included.</summary>
    public ParquetFilter Between(object low, object high) => Join(Compare(ComparisonKind.GreaterThanOrEqual, low).And(Compare(ComparisonKind.LessThanOrEqual, high)));

    /// <summary>The column holds one of the values (or is null, when one of them is null).</summary>
    /// <exception cref="ArgumentException">There are no values.</exception>
    public ParquetFilter In(params object?[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length == 0)
        {
            throw new ArgumentException("At least one value is needed.", nameof(values));
        }

        return Join(ParquetFilter.AnyOf(values.Select(EqualTo).ToArray()));
    }

    /// <summary>The column is null.</summary>
    public ParquetFilter IsNull() => Join(new NullFilter(_path, isNull: true));

    /// <summary>The column is not null.</summary>
    public ParquetFilter IsNotNull() => Join(new NullFilter(_path, isNull: false));

    /// <summary>The condition, joined by <c>and</c> or by <c>or</c> to the filter this column continues, when it continues one.</summary>
    private ParquetFilter Join(ParquetFilter condition)
    {
        if (_left is null)
        {
            return condition;
        }

        return _isAnd ? _left.And(condition) : _left.Or(condition);
    }

    private ParquetFilter EqualTo(object? value) => value is null ? new NullFilter(_path, isNull: true) : new ComparisonFilter(_path, ComparisonKind.Equal, value);

    private ComparisonFilter Compare(ComparisonKind kind, object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new ComparisonFilter(_path, kind, value);
    }
}
