using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Filtering;

/// <summary>
/// A condition on the values of columns, which a reader checks against the statistics of each row group (the smallest and largest
/// value, and how many are null) to skip the row groups that cannot hold a row that satisfies it. Build one with
/// <see cref="Column(string[])"/> and combine with <see cref="And"/>, <see cref="Or"/> and <see cref="Not"/>.
/// </summary>
/// <remarks>
/// <para>
/// A filter only prunes: a row group is skipped when its statistics prove that no row satisfies the filter, and kept when they say
/// that one may or cannot tell (no statistics, a column of the file the filter names but the file does not have, a column in a
/// collection). The rows of a kept row group are not filtered, so apply the condition to them as well when it must hold exactly.
/// </para>
/// <para>
/// A comparison with a null is false, and <see cref="Not"/> negates, so <c>Not(Equal(5))</c> is true for a null, as
/// <c>x != 5</c> is in C#. Values are compared as the column type defines: numbers, dates, times and decimals by value, text and
/// binary by their bytes (UTF-8 for text). Enums (stored by name) and <see cref="Guid"/>s can only be compared for equality;
/// a comparison for order with them keeps every row group.
/// </para>
/// </remarks>
public abstract class ParquetFilter
{
    private protected ParquetFilter()
    {
    }

    /// <summary>Starts a condition on a column, named by its path from just below the root: one name for a top-level column.</summary>
    /// <exception cref="ArgumentException">The path is empty or has an empty name.</exception>
    public static FilterColumn Column(params string[] path) => new(path);

    /// <summary>A condition that holds when all of the filters do.</summary>
    /// <exception cref="ArgumentException">There are no filters.</exception>
    public static ParquetFilter AllOf(params ParquetFilter[] filters) => Combine(filters, isAnd: true);

    /// <summary>A condition that holds when any of the filters does.</summary>
    /// <exception cref="ArgumentException">There are no filters.</exception>
    public static ParquetFilter AnyOf(params ParquetFilter[] filters) => Combine(filters, isAnd: false);

    public static ParquetFilter operator &(ParquetFilter left, ParquetFilter right) => left.And(right);

    public static ParquetFilter operator |(ParquetFilter left, ParquetFilter right) => left.Or(right);

    public static ParquetFilter operator !(ParquetFilter filter) => filter.Not();

    /// <summary>The columns the filter names, each as its path.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Columns
    {
        get
        {
            var paths = new List<IReadOnlyList<string>>();
            CollectColumns(paths);
            return paths;
        }
    }

    /// <summary>A condition that holds when this one and <paramref name="other"/> do.</summary>
    public ParquetFilter And(ParquetFilter other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new AndFilter([this, other]);
    }

    /// <summary>A condition that holds when this one or <paramref name="other"/> does.</summary>
    public ParquetFilter Or(ParquetFilter other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new OrFilter([this, other]);
    }

    /// <summary>A condition that holds when this one does not.</summary>
    public ParquetFilter Not() => Negate();

    /// <summary>The filter that holds when this one does not; a filter that negates another gives that one back.</summary>
    internal virtual ParquetFilter Negate() => new NotFilter(this);

    /// <summary>Whether a row group can hold a row that satisfies the filter, by its statistics.</summary>
    internal bool MayMatch(ParquetSchema schema, IReadOnlyList<ColumnOrder>? columnOrders, RowGroup rowGroup) =>
        Evaluate(new FilterContext(schema, columnOrders, rowGroup)) != FilterOutcome.None;

    internal abstract FilterOutcome Evaluate(FilterContext context);

    internal abstract void CollectColumns(List<IReadOnlyList<string>> paths);

    private static ParquetFilter Combine(ParquetFilter[] filters, bool isAnd)
    {
        ArgumentNullException.ThrowIfNull(filters);
        if (filters.Length == 0)
        {
            throw new ArgumentException("At least one filter is needed.", nameof(filters));
        }

        if (filters.Any(f => f is null))
        {
            throw new ArgumentException("A filter is null.", nameof(filters));
        }

        if (filters.Length == 1)
        {
            return filters[0];
        }

        return isAnd ? new AndFilter(filters.ToArray()) : new OrFilter(filters.ToArray());
    }
}
