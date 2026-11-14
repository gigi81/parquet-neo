using ParquetNeo.Filtering;

namespace ParquetNeo;

/// <summary>A column a condition is put on; see <see cref="ParquetFilter.Column(string[])"/>.</summary>
public sealed class FilterColumn
{
    private readonly string[] _path;

    internal FilterColumn(string[] path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0 || path.Any(string.IsNullOrEmpty))
        {
            throw new ArgumentException("A column is named by a path of names that are not empty.", nameof(path));
        }

        _path = (string[])path.Clone();
    }

    /// <summary>The column holds the value; for a null, the column is null.</summary>
    /// <param name="value">A value of a type that suits the column: a number for a number column, a <see cref="string"/> for text, a <see cref="DateTime"/> or <see cref="DateTimeOffset"/> for a timestamp, and so on.</param>
    /// <exception cref="ArgumentException">At evaluation: the value is of a type that does not suit the column.</exception>
    public ParquetFilter Equal(object? value) => value is null ? IsNull() : new ComparisonFilter(_path, ComparisonKind.Equal, value);

    /// <summary>The column holds another value, or is null; for a null, the column is not null.</summary>
    public ParquetFilter NotEqual(object? value) => Equal(value).Not();

    /// <summary>The column holds a value less than <paramref name="value"/>.</summary>
    public ParquetFilter LessThan(object value) => Compare(ComparisonKind.LessThan, value);

    /// <summary>The column holds a value less than or equal to <paramref name="value"/>.</summary>
    public ParquetFilter LessThanOrEqual(object value) => Compare(ComparisonKind.LessThanOrEqual, value);

    /// <summary>The column holds a value greater than <paramref name="value"/>.</summary>
    public ParquetFilter GreaterThan(object value) => Compare(ComparisonKind.GreaterThan, value);

    /// <summary>The column holds a value greater than or equal to <paramref name="value"/>.</summary>
    public ParquetFilter GreaterThanOrEqual(object value) => Compare(ComparisonKind.GreaterThanOrEqual, value);

    /// <summary>The column holds a value from <paramref name="low"/> to <paramref name="high"/>, both included.</summary>
    public ParquetFilter Between(object low, object high) => GreaterThanOrEqual(low).And(LessThanOrEqual(high));

    /// <summary>The column holds one of the values (or is null, when one of them is null).</summary>
    /// <exception cref="ArgumentException">There are no values.</exception>
    public ParquetFilter In(params object?[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length == 0)
        {
            throw new ArgumentException("At least one value is needed.", nameof(values));
        }

        return ParquetFilter.AnyOf(values.Select(Equal).ToArray());
    }

    /// <summary>The column is null.</summary>
    public ParquetFilter IsNull() => new NullFilter(_path, isNull: true);

    /// <summary>The column is not null.</summary>
    public ParquetFilter IsNotNull() => new NullFilter(_path, isNull: false);

    private ComparisonFilter Compare(ComparisonKind kind, object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new ComparisonFilter(_path, kind, value);
    }
}
