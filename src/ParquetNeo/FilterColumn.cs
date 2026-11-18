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

    /// <summary>
    /// The features of a GEOMETRY or GEOGRAPHY column may have a point in the rectangle from (<paramref name="xMin"/>, <paramref name="yMin"/>) to
    /// (<paramref name="xMax"/>, <paramref name="yMax"/>): the row groups whose bounding box does not meet it are skipped. Like every filter it only
    /// prunes: the features of the row groups that are kept are not tested, so test them yourself for an exact answer. A column of another
    /// type, or a row group without a recorded box, is kept. For a geography a rectangle with <paramref name="xMin"/> greater than
    /// <paramref name="xMax"/> crosses the antimeridian.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate is not a number, or <paramref name="yMin"/> is greater than <paramref name="yMax"/>.</exception>
    public ParquetFilter IntersectsBox(double xMin, double yMin, double xMax, double yMax)
    {
        if (double.IsNaN(xMin) || double.IsNaN(yMin) || double.IsNaN(xMax) || double.IsNaN(yMax))
        {
            throw new ArgumentOutOfRangeException(nameof(xMin), "A coordinate of the rectangle is not a number.");
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(yMin, yMax);
        return new BoundingBoxFilter(_path, xMin, yMin, xMax, yMax);
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
